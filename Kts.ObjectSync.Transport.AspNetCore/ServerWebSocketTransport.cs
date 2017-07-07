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
	public class ServerWebSocketTransport : ITransport
	{
		private readonly ICommonSerializer _serializer;
		public event Action<string, object> Receive;
		public event Action<ITransport> Connected;
		private readonly IActor<RecyclableMemoryStream, Task> _actor;
		private readonly List<WebSocket> _sockets = new List<WebSocket>();

		public ServerWebSocketTransport(ICommonSerializer serializer, double aggregationDelay = 0.0)
		{
			_serializer = serializer;
			var mainBuffer = (RecyclableMemoryStream)_mgr.GetStream("_MainServerBuffer");
			var msgType = _serializer.StreamsUtf8 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
			async Task setFunc(RecyclableMemoryStream stream, CancellationToken token, bool isFirst, bool isLast)
			{
				// if we're the first in a set but our buffer isn't empty, send it (should't happen with these actors)
				// if we're the first in a set make a new buffer
				// copy our current stream in to the buffer
				// if we're the last in the set ship the buffer
				// the far end needs to keep pulling packages out of the buffer as long as there are more bytes

				// how can we do this without two copies?
				// we can push the same stream in every time (make a new stream if we can't get the lock on the previous)
				// maybe we need a "unique value" actor

				isLast |= stream.Length == 0; // for flush

				try
				{
					if (isFirst && mainBuffer.Length > 0)
					{
						var buffer = new ArraySegment<byte>(mainBuffer.GetBuffer(), 0, (int)mainBuffer.Length);
						await SendAsync(buffer, msgType);
						mainBuffer.SetLength(0);
					}

					if (isFirst && isLast && mainBuffer.Length == 0)
					{
						var buffer = new ArraySegment<byte>(stream.GetBuffer(), 0, (int)stream.Length);
						await SendAsync(buffer, msgType);
						return;
					}

					stream.CopyTo(mainBuffer);

					if (isLast && mainBuffer.Length > 0)
					{
						var buffer = new ArraySegment<byte>(mainBuffer.GetBuffer(), 0, (int)mainBuffer.Length);
						await SendAsync(buffer, msgType);
						mainBuffer.SetLength(0);
					}
				}
				catch (TaskCanceledException)
				{
				}
				finally
				{
					stream.Dispose();
				}
			}

			_actor = aggregationDelay > 0.0 ? (IActor<RecyclableMemoryStream, Task>)new PeriodicAsyncActor<RecyclableMemoryStream, Task>(setFunc, TimeSpan.FromSeconds(aggregationDelay)) : new OrderedAsyncActor<RecyclableMemoryStream, Task>(setFunc);
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

		public async Task Send(string fullName, object value)
		{
			var package = new Package { Name = fullName, Data = value };
			var stream = (RecyclableMemoryStream)_mgr.GetStream(fullName);
			_serializer.Serialize(stream, package);
			var result = await _actor.Push(stream).ConfigureAwait(false);
			await result.ConfigureAwait(false);

		}

		public async Task Flush()
		{
			var result = await _actor.Push((RecyclableMemoryStream)_mgr.GetStream("_Flush"));
			await result;
		}

		private class OneTimeTransport : ITransport
		{
			private readonly WebSocket _socket;
			private readonly ICommonSerializer _serializer;

			public OneTimeTransport(WebSocket socket, ICommonSerializer serializer)
			{
				_socket = socket;
				_serializer = serializer;
			}

			public async Task Send(string fullName, object value)
			{
				var package = new Package { Name = fullName, Data = value };
				using (var stream = (RecyclableMemoryStream) _mgr.GetStream(fullName))
				{
					_serializer.Serialize(stream, package);
					var msgType = _serializer.StreamsUtf8 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
					var buffer = new ArraySegment<byte>(stream.GetBuffer(), 0, (int)stream.Length);
					await _socket.SendAsync(buffer, msgType, true, CancellationToken.None);
				}
			}

			public void Flush()
			{
				throw new NotSupportedException();
			}

			public event Action<string, object> Receive = delegate { };
			public event Action<ITransport> Connected = delegate { };
		}

		public class InnerServerMiddlewareTransport
		{
			private readonly RequestDelegate _next;
			private readonly ServerWebSocketTransport _parent;

			public InnerServerMiddlewareTransport(RequestDelegate next, ServerWebSocketTransport parent)
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
				_parent.Connected.Invoke(new OneTimeTransport(socket, _parent._serializer));
				await ReceiveForever(socket).ConfigureAwait(false);
				lock (_parent._sockets)
					_parent._sockets.Remove(socket);
			}

			private async Task ReceiveForever(WebSocket socket)
			{
				var array = new Byte[8192];

				try
				{
					using (var stream = _mgr.GetStream("_Receiver"))
					{
						while (socket.State == WebSocketState.Open)
						{
							stream.SetLength(0);
							WebSocketReceiveResult result;
							do
							{
								var buffer = new ArraySegment<byte>(array);
								result = await socket.ReceiveAsync(buffer, CancellationToken.None);
								if (result.CloseStatus != null)
								{
									// client's gone; be done with them; they will have to reinitiate comms
									return;
								}
								stream.Write(buffer.Array, buffer.Offset, result.Count);
							} while (!result.EndOfMessage);

							stream.Position = 0;
							while (stream.Position < stream.Length)
							{
								var package = _parent._serializer.Deserialize<Package>(stream);
								_parent.Receive.Invoke(package.Name, package.Data);
							}
						}
					}
				}
				catch (TaskCanceledException) { }
			}

		}
	}
}
