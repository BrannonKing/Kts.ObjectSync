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

namespace Kts.ObjectSync.Transport.AspNetCore
{
	// some ideas taken from: https://radu-matei.github.io/blog/aspnet-core-websockets-middleware/	
	public class ServerMiddlewareTransport: ITransport
	{
		private readonly ICommonSerializer _serializer;
		public event Action<string, object> Receive;

		public ServerMiddlewareTransport(ICommonSerializer serializer)
		{
			_serializer = serializer;
		}

		public IApplicationBuilder Attach(IApplicationBuilder builder, PathString path)
		{
			return builder.Map(path, app => app.UseMiddleware<InnerServerMiddlewareTransport>(this));
		}

		private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();
		
		public async Task Send(string fullName, object value)
		{
			var package = new Package { Name = fullName, Data = value };
			using (var stream = _mgr.GetStream(fullName))
			{
				_serializer.Serialize(stream, package);
				if (stream.TryGetBuffer(out var buffer))
				{
					var tasks = new List<Task>();
					lock (_sockets)
						foreach (var socket in _sockets)
						{
							tasks.Add(socket.SendAsync(buffer,
								_serializer.StreamsUtf8 ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
								true, CancellationToken.None));
						}
					await Task.WhenAll(tasks);
				}
			}
		}

		private readonly List<WebSocket> _sockets = new List<WebSocket>();

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
				lock(_parent._sockets)
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
