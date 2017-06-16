namespace Kts.ObjectSync.Common
{
	public abstract class ObjectForSynchronization
	{
		public abstract string ID { get; }

		protected virtual bool ShouldReceive(string fullPath)
		{
			return true;
		}

		protected virtual bool ShouldSend(string fullPath)
		{
			return true;
		}

		protected virtual bool ShouldSendOnConnected(string fullPath)
		{
			return false;
		}

	}
}