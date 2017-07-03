using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using CommonSerializer;
using Kts.ObjectSync.Common;
using Microsoft.IO;
using Kts.ActorsLite;

namespace Kts.ObjectSync.Transport.AspNetCore
{
	// some ideas taken from: https://radu-matei.github.io/blog/aspnet-core-websockets-middleware/	
	public class ServerMiddlewareTransport : ITransport
	{
		private readonly ICommonSerializer _serializer;
		public event Action<string, object> Receive;
		private readonly IActor<RecyclableMemoryStream> _actor;
		private readonly List<WebSocket> _sockets = new List<WebSocket>();

		public ServerMiddlewareTransport(ICommonSerializer serializer, Uri serverAddress, double reconnectDelay = 2.0, double aggregationDelay = 0.0)
		{
			_serializer = serializer;
			var periodMs = (int)Math.Round(TimeSpan.FromSeconds(aggregationDelay).TotalMilliseconds);
			var mainBuffer = _mgr.GetStream("MainClientBuffer");
			var msgType = _serializer.StreamsUtf8 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
			SetAction<RecyclableMemoryStream> action = async (stream, token, isFirst, isLast) =>
			{
			// if we're the first in a set but our buffer isn't empty, send it (should't happen with these actors)
			// if we're the first in a set make a new buffer
			// copy our current stream in to the buffer
			// if we're the last in the set ship the buffer
			// the far end needs to keep pulling packages out of the buffer as long as there are more bytes

			// how can we do this without two copies?
			// we can push the same stream in every time (make a new stream if we can't get the lock on the previous)
			// maybe we need a "unique value" actor

			try
				{
					ArraySegment<byte> buffer;
					if (isFirst && isLast && mainBuffer.Position == 0 && stream.TryGetBuffer(out buffer))
					{
						await SendAsync(buffer, msgType);
						return;
					}

					if (isFirst && mainBuffer.Position > 0 && mainBuffer.TryGetBuffer(out buffer))
					{
						await SendAsync(buffer, msgType);
						mainBuffer.Position = 0;
					}

					stream.CopyTo(mainBuffer);

					if (isLast && mainBuffer.Position > 0 && mainBuffer.TryGetBuffer(out buffer))
					{
						await SendAsync(buffer, msgType);
						mainBuffer.Position = 0;
					}
				}
				catch (TaskCanceledException)
				{
					return;
				}
				finally
				{
					stream.Dispose();
				}
			};
			_actor = periodMs > 0 ? (IActor<RecyclableMemoryStream>)new PeriodicAsyncActor<RecyclableMemoryStream>(action, periodMs) : new OrderedSyncActor<RecyclableMemoryStream>(action);
		}

		private async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType msgType)
		{
			var tasks = new List<Task>(_sockets.Count); // count may be off being outside the lock, but shouldn't be splinched
			lock (_sockets)
				foreach (var socket in _sockets)
					tasks.Add(socket.SendAsync(buffer, msgType, true, CancellationToken.None));
			await Task.WhenAll(tasks);
		}

		public IApplicationBuilder Attach(IApplicationBuilder builder, PathString path)
		{
			return builder.Map(path, app => app.UseMiddleware<InnerServerMiddlewareTransport>(this));
		}

		private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();

		public void Send(string fullName, object value)
		{
			var package = new Package { Name = fullName, Data = value };
			var stream = (RecyclableMemoryStream)_mgr.GetStream(fullName);
			_serializer.Serialize(stream, package);
			_actor.Push(stream);
		}

		public class InnerServerMiddlewareTransport
		{
			private readonly RequestDelegate _next;
			private readonly ServerMiddlewareTransport _parent;

			public InnerServerMiddlewareTransport(RequestDelegate next, ServerMiddlewareTransport parent)
			{
				_next = next;
				_parent = parent;
			}

			public async Task Invoke(HttpContext context)
			{
				if (!context.WebSockets.IsWebSocketRequest)
				{
					if (_next != null)
						await _next.Invoke(context);
					return;
				}

				var socket = await context.WebSockets.AcceptWebSocketAsync();
				lock (_parent._sockets)
					_parent._sockets.Add(socket);
				await ReceiveForever(socket);
				lock (_parent._sockets)
					_parent._sockets.Remove(socket);
			}

			private async Task ReceiveForever(WebSocket socket)
			{
				ArraySegment<Byte> buffer = new ArraySegment<byte>(new Byte[8192]);

				try
				{
					using (var stream = _mgr.GetStream("_Receiver"))
					{
						while (socket.State == WebSocketState.Open)
						{
							stream.Position = 0;
							WebSocketReceiveResult result;
							do
							{
								result = await socket.ReceiveAsync(buffer, CancellationToken.None);
								if (result.CloseStatus != null)
								{
									// client's gone; be done with them; they will have to reinitiate comms
									return;
								}
								stream.Write(buffer.Array, buffer.Offset, result.Count);
							} while (!result.EndOfMessage);

							stream.Position = 0;

							var package = _parent._serializer.Deserialize<Package>(stream);
							_parent.Receive.Invoke(package.Name, package.Data);
						}
					}
				}
				catch (TaskCanceledException) { }
			}

		}
	}
}
