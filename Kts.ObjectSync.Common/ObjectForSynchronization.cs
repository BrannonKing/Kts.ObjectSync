namespace Kts.ObjectSync.Common
{
	public abstract class ObjectForSynchronization
	{
        public const string WantsAllSuffix = "__WANTS_ALL";

        public abstract string ID { get; }

        protected internal virtual bool ShouldGetOnConnected { get { return true; } }

        protected internal virtual bool ShouldReceive(string fullPath)
		{
			return true;
		}

		protected internal virtual bool ShouldSend(string fullPath)
		{
			return true;
		}
	}
}