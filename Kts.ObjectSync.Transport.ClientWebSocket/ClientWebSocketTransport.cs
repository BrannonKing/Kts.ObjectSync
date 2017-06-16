using System;
using Kts.ObjectSync.Common;
using System.Threading.Tasks;
using System.Threading;
using System.Net.WebSockets;
using Microsoft.IO;

namespace Kts.ObjectSync.Transport.ClientWebSocket
{
    public class ClientWebSocketTransport: ITransport
    {
		private System.Net.WebSockets.ClientWebSocket _socket;
		private CancellationTokenSource _exitSource;

		private ClientWebSocketTransport(System.Net.WebSockets.ClientWebSocket socket)
		{
			_socket = socket;
			ReceiveForever();
		}

		private async void ReceiveForever()
		{
			ArraySegment<Byte> buffer = new ArraySegment<byte>(new Byte[8192]);

			WebSocketReceiveResult result = null;

			try
			{
				using (var stream = _mgr.GetStream("_Receiver"))
				{
					do
					{
						result = await _socket.ReceiveAsync(buffer, _exitSource.Token);
						if (_exitSource.IsCancellationRequested)
							return;
						if (result.CloseStatus != null)
						{
							Disconnected.Invoke(result.CloseStatusDescription);
							return;
						}
						stream.Write(buffer.Array, buffer.Offset, result.Count);
					}
					while (!result.EndOfMessage);

					stream.Position = 0;

					var package = _serializer.Deserialize<Package>(stream);
					Receive.Invoke(package.Name, package.Data);
				}
			}
			catch (TaskCanceledException) { }
		}

		public static async Task<ClientWebSocketTransport> Connect(Uri serverAddress, ICommonSerializer serializer)
		{
			var socket = new System.Net.WebSockets.ClientWebSocket();
			await socket.ConnectAsync(serverAddress, CancellationToken.None);
			if (socket.State != WebSocketState.Open)
				return null; // or exception? or do we want to loop and keep trying? and what if we get disconnected?
			return new ClientWebSocketTransport(socket);
		}

		public event Action<string, object> Receive = delegate { };
		public event Action<string> Disconnected = delegate { };

		private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();

		public void Send(string fullName, object value)
		{
			// TODO: this needs to go into one of our async task runners
			var package = new Package { Name = fullName, Data = value };
			using (var stream = _mgr.GetStream(fullName))
			{
				_serializer.Serialize(package, stream);
				if (stream.TryGetBuffer(out var buffer))
					await _socket.SendAsync(buffer);
			}
		}

		public void Dispose()
		{
			_exitSource.Cancel();
			_socket.Dispose();
		}
	}
}
