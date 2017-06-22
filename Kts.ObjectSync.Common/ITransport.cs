using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kts.ObjectSync.Common
{
	public interface ITransport
	{
		Task Send(string fullName, object value);
		event Action<string, object> Receive;
	}

	public sealed class Package
	{
		public IReadOnlyList<KeyValuePair<string, object>> Data { get; set; }
	}
}
