using System;
using Kts.ObjectSync.Common;
using System.Threading.Tasks;
using System.Threading;
using System.Net.WebSockets;

namespace Kts.ObjectSync.Transport.ClientWebSocket
{
    public class ClientWebSocketTransport: ITransport
    {
		private System.Net.WebSockets.ClientWebSocket _socket;

		private ClientWebSocketTransport(System.Net.WebSockets.ClientWebSocket socket) { _socket = socket; }

		public static async Task<ClientWebSocketTransport> Connect(Uri serverAddress, ICommonSerializer serializer)
		{
			var socket = new System.Net.WebSockets.ClientWebSocket();
			await socket.ConnectAsync(serverAddress, CancellationToken.None);
			return new ClientWebSocketTransport(socket);
		}

		public event Action<string, object> Receive;
		public event Action<ConnectionState> ConnectionStateChanged;

		public void Send(string fullName, object value)
		{
			var data = serializer.Serialize(value);
			_socket.SendAsync()
		}

		public void Dispose()
		{
			_socket.Dispose();
		}
	}
}
