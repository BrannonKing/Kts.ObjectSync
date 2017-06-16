using System;

namespace Kts.ObjectSync.Common
{
	public enum ConnectionState { Disconnected, Disconnecting, Connecting, Connected }
	public interface ITransport: IDisposable
	{
		void Send(string fullName, object value);
		event Action<string, object> Receive;
		event Action<ConnectionState> ConnectionStateChanged;
	}
}
