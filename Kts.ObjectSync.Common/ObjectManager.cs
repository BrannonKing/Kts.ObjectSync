﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;

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

	}

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

		    var rootNode = new PropertyNode(_transport, objectForSynchronization.ID, objectForSynchronization);
		    if (!_nodeCache.TryAdd(objectForSynchronization.ID, rootNode))
			    throw new ArgumentException($"Object {objectForSynchronization.ID} added twice. Make sure IDs differ between objects.");
	    }

	    private class ObjectForSynchronizationWrapper : ObjectForSynchronization
	    {
		    public ObjectForSynchronizationWrapper(string id, object child)
		    {
				ID = id;
				Child = child;
		    }

		    public override string ID { get; }
			public object Child { get; }
	    }


		public void Add(string id, object objectForSynchronization)
		{
			Add(new ObjectForSynchronizationWrapper(id, objectForSynchronization));
		}

	    public void Remove(string idOfObjectForSynchronization)
	    {
		    if (_nodeCache.TryRemove(idOfObjectForSynchronization, out var node))
			    node.Dispose(); // disposes all children as well
	    }

	    public void Remove(ObjectForSynchronization objectForSynchronization)
	    {
		    Remove(objectForSynchronization.ID);
	    }
    }
}
