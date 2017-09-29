using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using CommonSerializer;
using Kts.ObjectSync.Common;
using Microsoft.IO;
using NetMQ;
using NetMQ.Sockets;
using System.Threading.Tasks;

namespace Kts.ObjectSync.Transport.NetMQ
{
    public class NetMQTransport: ITransport, IDisposable
    {
	    private readonly ICommonSerializer _serializer;
	    private readonly RouterSocket _socket;
	    private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();
	    private const string _prefix = "ObjectSyncProperty.";
	    private readonly ConcurrentDictionary<string, Tuple<Type, Action<string, object>>> _receiverCache = new ConcurrentDictionary<string, Tuple<Type, Action<string, object>>>();
        private readonly ConcurrentDictionary<string, Action> _getOnConnectCache = new ConcurrentDictionary<string, Action>();
        private readonly NetMQPoller _poller;

        public NetMQTransport(ICommonSerializer serializer, bool isServer, Uri serverAddress = null, int maxMessageBufferCount = 1000)
	    {
		    _serializer = serializer;

		    _socket = new RouterSocket();
		    _socket.Options.ReceiveHighWatermark = maxMessageBufferCount;
            // _subscription.Options.Linger // keep messages after disconnect
            var timer = new NetMQTimer(5);
            _poller = new NetMQPoller { _socket, timer };
            _socket.ReceiveReady += OnMessageHandler;
            var address = serverAddress == null ? "tcp://localhost:12345" : serverAddress.ToString();
            if (isServer)
			    _socket.Bind(address);
		    else
		    	_socket.Connect(address);

            _poller.RunAsync("NetMQPoller for " + address);
        }

        public void Flush() // TODO: add timeout to flush
        {
            //var task = new Task(() => {
            //    while (_socket.HasOut) // doesn't work as it's "always true for the publisher" -- some bug
            //        Thread.Sleep(1);
            //});
            //task.Start(_poller);
            //task.Wait();
	    }

	    private void OnMessageHandler(object sender, NetMQSocketEventArgs e)
	    {
            var msg = new NetMQMessage(3);
            var received = -1;
            while (e.Socket.TryReceiveMultipartMessage(ref msg) && ++received < 1000)
            {
                var subject = msg[1].ConvertToString().Replace(_prefix, ""); // three frames: connection, subject, data
                if (!_receiverCache.TryGetValue(subject, out var tuple))
                    continue;

                using (var ms = new MemoryStream(msg[2].Buffer, 0, msg[2].BufferSize, false))
                {
                    var data = ms.Length <= 0 ? null : _serializer.Deserialize(ms, tuple.Item1);
                    tuple.Item2.Invoke(subject, data);
                }
            }
	    }
		
	    public void Dispose()
	    {
		    _socket.ReceiveReady -= OnMessageHandler;
            _poller.Dispose();
		    _socket.Dispose();
	    }

	    public void Send(string fullKey, Type type, object value) // TODO: add timeout to send
	    {
			using (var stream = (RecyclableMemoryStream)_mgr.GetStream(fullKey))
			{
				_serializer.Serialize(stream, value, type);
				var connectionFrame = new NetMQFrame(0); // all connections
				var subjectFrame = new NetMQFrame(_prefix + fullKey);
				var dataFrame = new NetMQFrame(stream.GetBuffer(), (int)stream.Length);
				var msg = new NetMQMessage(new[]{connectionFrame, subjectFrame, dataFrame});
				_socket.SendMultipartMessage(msg); // does memcpy, will block if buffer is full
			}
		}

		public void RegisterReceiver(string parentKey, Type type, Action<string, object> action)
	    {
		    _receiverCache[parentKey] = Tuple.Create(type, action);
	    }

	    public void UnregisterReceiver(string parentKey)
	    {
		    _receiverCache.TryRemove(parentKey, out var _);
	    }

        public void RegisterWantsAllOnConnected(string fullKey)
        {
	        throw new NotImplementedException();
            //_getOnConnectCache[fullKey] = action;
            //if (IsConnected)
            //    action.Invoke();
        }

        public void UnregisterWantsAllOnConnected(string fullKey)
        {
            _getOnConnectCache.TryRemove(fullKey, out var _);
        }
    }
}