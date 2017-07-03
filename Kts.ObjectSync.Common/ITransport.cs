using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kts.ObjectSync.Common
{
	public interface ITransport
	{
		void Send(string fullName, object value);
		event Action<string, object> Receive;
	}

	public sealed class Package
	{
		public string Name { get; set; }
		public object Data { get; set; }
	}
}
