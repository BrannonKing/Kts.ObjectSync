using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kts.ObjectSync
{
	public interface IObjectForSynchronization : INotifyPropertyChanged
	{
		string ID { get; }
	}

	public abstract class ObjectUpdater
	{
		internal ObjectUpdater()
		{
			// you no can use
		}
		protected abstract void HandleUpdate(string id, string[] propertyPath, string value);

	}

    public class ObjectManager: ObjectUpdater
    {
	    public ObjectManager(IEnumerable<IObjectForSynchronization> objectsForSynchronization)
	    {
		    foreach (var ofs in objectsForSynchronization)
			    Add(ofs);
	    }

	    public ObjectManager()
	    {
	    }

	    public event Action<ConnectionState> ConnectionStateChanged = delegate { };

	    private readonly ConcurrentDictionary<string, PropertyNode> _nodeCache = new ConcurrentDictionary<string, PropertyNode>();

	    public void Add(IObjectForSynchronization objectForSynchronization)
	    {
		    if (objectForSynchronization == null)
			    throw new ArgumentNullException(nameof(objectForSynchronization));

		    var rootNode = new PropertyNode(objectForSynchronization, objectForSynchronization is ObjectForSynchronizationWrapper);
		    if (!_nodeCache.TryAdd(objectForSynchronization.ID, rootNode))
			    throw new ArgumentException($"Object {objectForSynchronization.ID} added twice. Make sure IDs differ between objects.");
	    }

	    private class ObjectForSynchronizationWrapper : IObjectForSynchronization
	    {
		    public ObjectForSynchronizationWrapper(string id, object child)
		    {
				ID = id;
				Child = child;
		    }

			public event PropertyChangedEventHandler PropertyChanged;
		    public string ID { get; }

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

	    public void Remove(IObjectForSynchronization objectForSynchronization)
	    {
		    Remove(objectForSynchronization.ID);
	    }

	    protected override void HandleUpdate(string id, string[] propertyPath, string value)
	    {
		    if (_nodeCache.TryGetValue(id, out var node))
			    node.Update(propertyPath, value);
	    }
    }
}
