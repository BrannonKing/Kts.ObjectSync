using System;
using System.Threading.Tasks;

namespace Kts.ObjectSync.Common
{
	public interface ITransport
	{
		Task Send(string fullName, object value);
		event Action<string, object> Receive;
		event Action<ITransport> Connected;
	}

	public sealed class Package
	{
		public string Name { get; set; }
		public object Data { get; set; }
	}
}
