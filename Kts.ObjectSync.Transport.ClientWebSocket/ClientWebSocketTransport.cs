using System;
using System.Collections.Concurrent;
using Kts.ObjectSync.Common;
using System.Threading.Tasks;
using System.Threading;
using System.Net.WebSockets;
using System.Text;
using CommonSerializer;
using Microsoft.IO;
using Kts.ActorsLite;

namespace Kts.ObjectSync.Transport.ClientWebSocket
{
	public class ClientWebSocketTransport : ITransport, IDisposable
	{
		protected System.Net.WebSockets.ClientWebSocket _socket;
		protected readonly ICommonSerializer _serializer;
		protected readonly CancellationTokenSource _exitSource = new CancellationTokenSource();
		protected TaskCompletionSource<bool> _connectionCompletionSource = new TaskCompletionSource<bool>();
		protected readonly IActor<RecyclableMemoryStream, Task> _actor;
		private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();
		private readonly ConcurrentDictionary<string, Tuple<Type, Action<string, object>>> _cache = new ConcurrentDictionary<string, Tuple<Type, Action<string, object>>>();

		public ClientWebSocketTransport(ICommonSerializer serializer, Uri serverAddress, double reconnectDelay = 2.0, double aggregationDelay = 0.0)
		{
			_serializer = serializer;
			var mainBuffer = (RecyclableMemoryStream)_mgr.GetStream("_MainClientBuffer");
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
						var buffer = new ArraySegment<byte>(mainBuffer.GetBuffer(), 0, (int) mainBuffer.Length);
						await _socket.SendAsync(buffer, msgType, true, _exitSource.Token);
						mainBuffer.SetLength(0);
					}

					if (isFirst && isLast && mainBuffer.Length == 0)
					{
						var buffer = new ArraySegment<byte>(stream.GetBuffer(), 0, (int) stream.Length);
						await _socket.SendAsync(buffer, msgType, true, _exitSource.Token);
						return;
					}

					stream.CopyTo(mainBuffer);

					if (isLast && mainBuffer.Length > 0)
					{
						var buffer = new ArraySegment<byte>(mainBuffer.GetBuffer(), 0, (int) mainBuffer.Length);
						await _socket.SendAsync(buffer, msgType, true, _exitSource.Token);
						mainBuffer.SetLength(0);
					}
				}
				catch (TaskCanceledException)
				{
				}
				catch
				{
					
				}
				finally
				{
					stream.Dispose();
				}
			}

			_actor = aggregationDelay > 0.0 ? (IActor<RecyclableMemoryStream, Task>)new PeriodicAsyncActor<RecyclableMemoryStream, Task>(setFunc, TimeSpan.FromSeconds(aggregationDelay)) : new OrderedAsyncActor<RecyclableMemoryStream, Task>(setFunc);
			ConnectForever(serverAddress, reconnectDelay);
		}

		public Task HasConnected => _connectionCompletionSource.Task;

		private async void ConnectForever(Uri serverAddress, double reconnectDelay)
		{
			while (!_exitSource.IsCancellationRequested)
			{
				try
				{
					_socket?.Dispose();
					try
					{
						_socket = new System.Net.WebSockets.ClientWebSocket();
						await _socket.ConnectAsync(serverAddress, _exitSource.Token);
					}
					catch (WebSocketException)
					{
						// relying on state handling instead
					}
					if (_socket?.State == WebSocketState.Open)
					{
						_connectionCompletionSource.SetResult(true);
						try
						{
							await ReceiveForever();
						}
						finally
						{
							_connectionCompletionSource = new TaskCompletionSource<bool>();
						}
					}
					if (_exitSource.IsCancellationRequested)
						break;
					if (reconnectDelay > 0.0)
						await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), _exitSource.Token);
				}
				catch (TaskCanceledException)
				{
					if (_exitSource.IsCancellationRequested)
						break;
				}
			}
			_socket?.Dispose();
		}

		private async Task ReceiveForever()
		{
			var array = new Byte[8192];

			try
			{
				using (var stream = (RecyclableMemoryStream)_mgr.GetStream("_Receiver"))
				{
					while (_socket.State == WebSocketState.Open)
					{
						stream.SetLength(0);
						WebSocketReceiveResult result;
						do
						{
							var buffer = new ArraySegment<byte>(array);
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

						while (stream.Position < stream.Length - 4)
						{
							var subjectLen = BitConverter.ToInt32(stream.GetBuffer(), (int) stream.Position);
							stream.Position += 4;
							var subject = Encoding.UTF8.GetString(stream.GetBuffer(), (int) stream.Position, subjectLen);
							stream.Position += subjectLen;

							_cache.TryGetValue(subject, out var tuple);
							if (tuple == null)
							{
								_serializer.Deserialize<object>(stream);
								continue;
							}
							var data = _serializer.Deserialize(stream, tuple.Item1);
							tuple.Item2.Invoke(subject, data);							
						}
					}
				}
			}
			catch (TaskCanceledException) { }
		}

		public async Task Flush()
		{
			var result = await _actor.Push((RecyclableMemoryStream) _mgr.GetStream("_Flush")).ConfigureAwait(false);
			await result.ConfigureAwait(false);
		}

		private Action<ITransport> _connected;
		public event Action<ITransport> Connected
		{
			add
			{
				_connected = (Action<ITransport>)Delegate.Combine(_connected, value);
				value.Invoke(this);
			}
			remove => _connected = (Action<ITransport>)Delegate.Remove(_connected, value);
		}

		public void Dispose()
		{
			_exitSource.Cancel();
		}

		public int SendQueueCount => ((dynamic) _actor).ScheduledTasksCount;

		public void Send(string fullKey, Type type, object value)
		{
			// we have to serialize it during the call to make sure that we get the right value
			// if we lock the one big stream, we can't have multiple simultaneous serializations
			// if we make a bunch of small streams and copy them all then we can
			// it's a performance trade-off for multiple simulatenous writers vs reducing the memcpy by one
			var stream = (RecyclableMemoryStream)_mgr.GetStream(fullKey);
			var subject = Encoding.UTF8.GetBytes(fullKey);
			var header = BitConverter.GetBytes(subject.Length);
			stream.Write(header, 0, header.Length);
			stream.Write(subject, 0, subject.Length);
			_serializer.Serialize(stream, value, type);
			_actor.Push(stream);
		}

		public void RegisterReceiver(string parentKey, Type type, Action<string, object> action)
		{
			_cache[parentKey] = Tuple.Create(type, action);
		}

		public void UnregisterReceiver(string parentKey)
		{
			var ret = _cache.TryRemove(parentKey, out var _);
			System.Diagnostics.Debug.Assert(ret);
		}
	}
}
