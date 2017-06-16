using System;
using Kts.ObjectSync.Common;
using System.Threading.Tasks;
using System.Threading;
using System.Net.WebSockets;
using CommonSerializer;
using Microsoft.IO;

namespace Kts.ObjectSync.Transport.ClientWebSocket
{
    public class ClientWebSocketTransport: ITransport, IDisposable
    {
		private readonly System.Net.WebSockets.ClientWebSocket _socket;
	    private readonly ICommonSerializer _serializer;
	    private readonly CancellationTokenSource _exitSource = new CancellationTokenSource();
		private readonly TaskCompletionSource<bool> _completionSource = new TaskCompletionSource<bool>();

		private ClientWebSocketTransport(System.Net.WebSockets.ClientWebSocket socket, ICommonSerializer serializer)
		{
			_socket = socket;
			_serializer = serializer;
			ReceiveForever();
		}

	    public Task Disconnection => _completionSource.Task;

	    private async void ReceiveForever()
		{
			ArraySegment<Byte> buffer = new ArraySegment<byte>(new Byte[8192]);

			try
			{
				using (var stream = _mgr.GetStream("_Receiver"))
				{
					while (_socket.State == WebSocketState.Open)
					{
						stream.Position = 0;
						WebSocketReceiveResult result;
						do
						{
							result = await _socket.ReceiveAsync(buffer, _exitSource.Token);
							if (_exitSource.IsCancellationRequested)
								return;
							if (result.CloseStatus != null)
							{
								_completionSource.SetException(new Exception("Socket connection lost. Message: " + result.CloseStatusDescription));
								return;
							}
							stream.Write(buffer.Array, buffer.Offset, result.Count);
						} while (!result.EndOfMessage);

						stream.Position = 0;

						var package = _serializer.Deserialize<Package>(stream);
						Receive.Invoke(package.Name, package.Data);
					}
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
			return new ClientWebSocketTransport(socket, serializer);
		}

		public event Action<string, object> Receive = delegate { };

		private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();

		public async Task Send(string fullName, object value)
		{
			// TODO: this needs to go into one of our async task runners
			var package = new Package { Name = fullName, Data = value };
			using (var stream = _mgr.GetStream(fullName))
			{
				_serializer.Serialize(stream, package);
				if (stream.TryGetBuffer(out var buffer))
					await _socket.SendAsync(buffer, 
						_serializer.StreamsUtf8 ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
						true, _exitSource.Token);
			}
		}

		public void Dispose()
		{
			_exitSource.Cancel();
			_completionSource.SetResult(true);
			_socket.Dispose();
		}
	}
}
