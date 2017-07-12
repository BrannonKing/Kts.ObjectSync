using System;
using System.IO;
using System.Threading.Tasks;
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
	    private const string _prefix = "_ObjectSync.";

		public NatsTransport(ICommonSerializer serializer, Uri serverAddress = null)
	    {
		    _serializer = serializer;
		    var cf = new ConnectionFactory();
		    var options = ConnectionFactory.GetDefaultOptions();
		    if (serverAddress != null)
			    options.Servers = new []{ serverAddress.ToString() };
		    else
			    options.Servers = new []{ "localhost:4222" };
		    options.AllowReconnect = true;
			_connection = cf.CreateConnection(options);
		    _subscription = _connection.SubscribeAsync(_prefix + ">");
		    _subscription.MessageHandler += OnMessageHandler;
		    _subscription.Start();
	    }

	    public bool IsConnected => _connection.State == ConnState.CONNECTED;

	    public void Flush()
	    {
		    _connection.Flush();
	    }

	    private void OnMessageHandler(object sender, MsgHandlerEventArgs e)
	    {
		    using (var ms = new MemoryStream(e.Message.Data, false))
		    {
			    while (ms.Position < ms.Length)
			    {
				    var package = _serializer.Deserialize<Package>(ms);
				    Receive?.Invoke(package.Name ?? e.Message.Subject.Replace(_prefix, ""), package.Data);
			    }
		    }
	    }

	    public async Task Send(string fullName, object value)
	    {
		    var package = new Package {Data = value};
		    using (var stream = (RecyclableMemoryStream) _mgr.GetStream(fullName))
		    {
				_serializer.Serialize(stream, package);
				_connection.Publish(_prefix + fullName, stream.ToArray());
		    }
	    }

	    public event Action<string, object> Receive;
	    public event Action<ITransport> Connected;
	    public void Dispose()
	    {
		    _subscription.MessageHandler -= OnMessageHandler;
		    _subscription.Dispose();
			_connection.Dispose();
	    }
    }
}
