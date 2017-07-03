using System;
using Kts.ObjectSync.Common;
using System.Threading.Tasks;
using System.Threading;
using System.Net.WebSockets;
using CommonSerializer;
using Microsoft.IO;
using Kts.ActorsLite;

namespace Kts.ObjectSync.Transport.ClientWebSocket
{
	public class ClientWebSocketTransport : ITransport, IDisposable
	{
		private readonly System.Net.WebSockets.ClientWebSocket _socket;
		private readonly ICommonSerializer _serializer;
		private readonly CancellationTokenSource _exitSource = new CancellationTokenSource();
		private readonly IActor<RecyclableMemoryStream> _actor;

		public ClientWebSocketTransport(ICommonSerializer serializer, Uri serverAddress, double reconnectDelay = 2.0, double aggregationDelay = 0.0)
		{
			_serializer = serializer;
			_socket = new System.Net.WebSockets.ClientWebSocket();
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
						await _socket.SendAsync(buffer, msgType, true, _exitSource.Token);
						return;
					}

					if (isFirst && mainBuffer.Position > 0 && mainBuffer.TryGetBuffer(out buffer))
					{
						await _socket.SendAsync(buffer, msgType, true, _exitSource.Token);
						mainBuffer.Position = 0;
					}

					stream.CopyTo(mainBuffer);

					if (isLast && mainBuffer.Position > 0 && mainBuffer.TryGetBuffer(out buffer))
					{
						await _socket.SendAsync(buffer, msgType, true, _exitSource.Token);
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

						while (stream.Position < stream.Length)
						{
							var package = _serializer.Deserialize<Package>(stream); // it may be more efficient to group them all into one large package and only call the deserializer once
							if (package.Name != null)
								Receive.Invoke(package.Name, package.Data);
						}
					}
				}
			}
			catch (TaskCanceledException) { }
		}

		public event Action<string, object> Receive = delegate { };

		private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();

		public void Send(string fullName, object value)
		{
			// we have to serialize it during the call to make sure that we get the right value
			// if we lock the one big stream, we can't have multiple simultaneous serializations
			// if we make a bunch of small streams and copy them all then we can
			// it's a performance trade-off for multiple simulatenous writers vs reducing the memcpy by one
			var package = new Package { Name = fullName, Data = value };
			var stream = (RecyclableMemoryStream)_mgr.GetStream(fullName);
			_serializer.Serialize(stream, package);
			_actor.Push(stream);
		}

		public void Dispose()
		{
			_exitSource.Cancel();
		}
	}
}
