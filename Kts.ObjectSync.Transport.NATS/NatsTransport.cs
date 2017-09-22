using System;
using System.Collections.Concurrent;
using System.IO;
using CommonSerializer;
using Kts.ObjectSync.Common;
using Microsoft.IO;
using NATS.Client;

namespace Kts.ObjectSync.Transport.NATS
{
    public class NatsTransport: ITransport, IDisposable
    {
	    private readonly ICommonSerializer _serializer;
	    private readonly IConnection _connection;
	    private readonly IAsyncSubscription _subscription;
	    private static readonly RecyclableMemoryStreamManager _mgr = new RecyclableMemoryStreamManager();
	    private const string _prefix = "ObjectSyncProperty.";
	    private readonly ConcurrentDictionary<string, Tuple<Type, Action<string, object>>> _receiverCache = new ConcurrentDictionary<string, Tuple<Type, Action<string, object>>>();
        private readonly ConcurrentDictionary<string, Action> _getOnConnectCache = new ConcurrentDictionary<string, Action>();


        public NatsTransport(ICommonSerializer serializer, Uri serverAddress = null)
	    {
		    _serializer = serializer;
		    var cf = new ConnectionFactory();
		    var options = ConnectionFactory.GetDefaultOptions();
		    if (serverAddress != null)
			    options.Servers = new []{ serverAddress.ToString() };
		    else
			    options.Servers = new []{ "localhost:4222" };
		    options.SubChannelLength = 65536 * 10;
			options.AllowReconnect = true; // default attempt lasts 2 seconds
		    options.MaxReconnect = int.MaxValue; // retry connection forever; our buffers may get full at some point
		    options.ReconnectWait = 1500;
		    options.AsyncErrorEventHandler = OnAsyncError;
            options.ReconnectedEventHandler = OnReconnected;
			//options.
			_connection = cf.CreateConnection(options);
		    _subscription = _connection.SubscribeAsync(_prefix + ">");
		    _subscription.MessageHandler += OnMessageHandler;
		    _subscription.Start();
	    }

        private void OnAsyncError(object sender, ErrEventArgs e)
	    {
		    System.Diagnostics.Debug.WriteLine(e.Error);
	    }

	    public bool IsConnected => _connection.State == ConnState.CONNECTED;

	    public void Flush()
	    {
		    _connection.Flush();
	    }

	    private void OnMessageHandler(object sender, MsgHandlerEventArgs e)
	    {
		    var subject = e.Message.Subject.Replace(_prefix, "");
		    if (!_receiverCache.TryGetValue(subject, out var tuple))
			    return;

		    using (var ms = new MemoryStream(e.Message.Data, false))
		    {
				var data = ms.Length <= 0 ? null : _serializer.Deserialize(ms, tuple.Item1);
				tuple.Item2.Invoke(subject, data);
		    }
	    }
		
	    public void Dispose()
	    {
		    _subscription.MessageHandler -= OnMessageHandler;
		    _subscription.Dispose();
			_connection.Dispose();
	    }

	    public void Send(string fullKey, Type type, object value)
	    {
			using (var stream = (RecyclableMemoryStream)_mgr.GetStream(fullKey))
			{
				_serializer.Serialize(stream, value, type);
				_connection.Publish(_prefix + fullKey, stream.ToArray());
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

        private void OnReconnected(object sender, ConnEventArgs e)
        {
            // request all the first-time transfers
            foreach (var action in _getOnConnectCache.Values)
                action.Invoke();
        }

        public void RegisterWantsAllOnConnected(string fullKey)
        {
            Action action = () => _connection.Publish(_prefix + fullKey + ObjectForSynchronization.WantsAllSuffix, new byte[0]);
            _getOnConnectCache[fullKey] = action;
            if (IsConnected)
                action.Invoke();
        }

        public void UnregisterWantsAllOnConnected(string fullKey)
        {
            _getOnConnectCache.TryRemove(fullKey, out var _);
        }
    }
}
