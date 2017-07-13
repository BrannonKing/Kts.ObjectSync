using System;

namespace Kts.ObjectSync.Common
{
	public interface ITransport
	{
		// The Send & Register methods should use type <T>, but I don't have the kind of interface in FastMember
		void Send(string fullKey, Type type, object value); // don't block; throw an exception if the queue is full (and expose the queue count and capacity at the transport level)
		void RegisterReceiver(string fullKey, Type type, Action<string, object> action);
		void UnregisterReceiver(string fullKey);
	}

	public sealed class Package
	{
		public string Name { get; set; }
		public object Data { get; set; }
	}
}
