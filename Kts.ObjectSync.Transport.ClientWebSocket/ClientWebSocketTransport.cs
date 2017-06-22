using System;
using Kts.ObjectSync.Common;
using System.Threading.Tasks;
using System.Threading;
using System.Net.WebSockets;
using CommonSerializer;
using Microsoft.IO;

namespace Kts.ObjectSync.Transport.ClientWebSocket
{
	public class ClientWebSocketTransport : ITransport, IDisposable
	{
		private readonly System.Net.WebSockets.ClientWebSocket _socket;
		private readonly ICommonSerializer _serializer;
		private readonly CancellationTokenSource _exitSource = new CancellationTokenSource();

		public ClientWebSocketTransport(ICommonSerializer serializer, Uri serverAddress, double reconnectDelay = 2.0, double aggregationDelay = 0.0)
		{
			_serializer = serializer;
			_socket = new System.Net.WebSockets.ClientWebSocket();
			ConnectForever(serverAddress, reconnectDelay);
		}

		private async void ConnectForever(Uri serverAddress, double reconnectDelay)
		{
			while (!_exitSource.IsCancellationRequested)
			{
				try
				{
					await _socket.ConnectAsync(serverAddress, _exitSource.Token);
					if (_socket.State == WebSocketState.Open)
						await ReceiveForever();
					if (_exitSource.IsCancellationRequested)
						break;
					if (reconnectDelay > 0.0)
						await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), _exitSource.Token);
				}
				catch (TaskCanceledException) { }
			}
			_socket.Dispose();
		}

		private async Task ReceiveForever()
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
								System.Diagnostics.Debug.WriteLine("Socket connection lost. Message: {0}", result.CloseStatusDescription);
								return;
							}
							stream.Write(buffer.Array, buffer.Offset, result.Count);
						} while (!result.EndOfMessage);

						stream.Position = 0;

						var package = _serializer.Deserialize<Package>(stream);
						if (package.Data != null)
							foreach(var kvp in package.Data)
								Receive.Invoke(kvp.Key, kvp.Value);
					}
				}
			}
			catch (TaskCanceledException) { }
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
		}
	}
}
