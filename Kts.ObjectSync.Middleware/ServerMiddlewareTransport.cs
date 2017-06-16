using Kts.ObjectSync.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;

namespace Kts.ObjectSync.Transport.AspNetCore
{
	// some ideas taken from: https://radu-matei.github.io/blog/aspnet-core-websockets-middleware/	
	public class ServerMiddlewareTransport: ITransport
	{
		public event Action<string, object> Receive;
		public event Action<string> Disconnected;

		public IApplicationBuilder Attach(IApplicationBuilder builder, PathString path)
		{
			return builder.Map(path, app => app.UseMiddleware<InnerServerMiddlewareTransport>());
		}

		public void Dispose()
		{
			
		}

		public void Send(string fullName, object value)
		{
			throw new NotImplementedException();
		}

		public class InnerServerMiddlewareTransport
		{
			private readonly RequestDelegate _next;
			private readonly ObjectManager _manager;

			public InnerServerMiddlewareTransport(RequestDelegate next, ObjectManager manager)
			{
				_next = next;
				_manager = manager;
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
				await _webSocketHandler.OnConnected(socket);

				await ReceiveForever(socket, async (result, buffer) =>
				{
					if (result.MessageType == WebSocketMessageType.Text)
					{
						await _webSocketHandler.ReceiveAsync(socket, result, buffer);
						return;
					}
					else if (result.MessageType == WebSocketMessageType.Binary)
					{

					}
					else if (result.MessageType == WebSocketMessageType.Close)
					{
						await _webSocketHandler.OnDisconnected(socket);
						return;
					}
					else throw new NotImplementedException();
				});
			}

			private async Task ReceiveForever(WebSocket socket, Action<WebSocketReceiveResult, byte[]> handleMessage)
			{
				var buffer = new byte[1024 * 8];

				while (socket.State == WebSocketState.Open)
				{
					var result = await socket.ReceiveAsync(buffer: new ArraySegment<byte>(buffer), CancellationToken.None);
					handleMessage(result, buffer);
				}
			}
		}
	}
}
