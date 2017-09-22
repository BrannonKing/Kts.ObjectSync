using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Kts.ObjectSync.Common
{
	public class ObjectManager
    {
		private readonly ITransport _transport;
	    public ObjectManager(ITransport transport, IEnumerable<ObjectForSynchronization> objectsForSynchronization = null)
	    {
			_transport = transport;
			if (objectsForSynchronization != null)
				foreach (var ofs in objectsForSynchronization)
					Add(ofs);
	    }

	    private readonly ConcurrentDictionary<string, PropertyNode> _nodeCache = new ConcurrentDictionary<string, PropertyNode>();

	    public void Add(ObjectForSynchronization objectForSynchronization)
	    {
		    if (objectForSynchronization == null)
			    throw new ArgumentNullException(nameof(objectForSynchronization));

		    var rootNode = new PropertyNode(_transport, objectForSynchronization);
		    if (!_nodeCache.TryAdd(objectForSynchronization.ID, rootNode))
			    throw new ArgumentException($"Object {objectForSynchronization.ID} added twice. Make sure IDs differ between objects.");

			if (objectForSynchronization.ShouldGetOnConnected)
				_transport.RegisterWantsAllOnConnected(objectForSynchronization.ID);
	    }

	    private class ObjectForSynchronizationWrapper : ObjectForSynchronization
	    {
            public ObjectForSynchronizationWrapper(string id, object child, bool getAllDataOnConnected)
		    {
			    ID = id;
                ShouldGetOnConnected = getAllDataOnConnected;
                Child = child;
            }

            public override string ID { get; }
            protected internal override bool ShouldGetOnConnected { get; }

            // ReSharper disable once UnusedAutoPropertyAccessor.Local
            public object Child { get; }

        }


		public void Add(string id, object objectForSynchronization, bool getAllDataOnConnected = true)
		{
			Add(new ObjectForSynchronizationWrapper(id, objectForSynchronization, getAllDataOnConnected));
		}

	    public void Remove(string idOfObjectForSynchronization)
	    {
		    if (_nodeCache.TryRemove(idOfObjectForSynchronization, out var node))
		    {
			    _transport.UnregisterWantsAllOnConnected(idOfObjectForSynchronization);
			    node.Dispose(); // disposes all children as well
		    }
	    }

	    public void Remove(ObjectForSynchronization objectForSynchronization)
	    {
		    Remove(objectForSynchronization.ID);
	    }
    }
}
