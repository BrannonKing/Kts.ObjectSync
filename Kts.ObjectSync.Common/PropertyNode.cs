using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kts.ObjectSync.Common
{
	class PropertyNode: IDisposable
    {
	    private readonly object _obj;
		private readonly FastMember.TypeAccessor _accessor;
		private readonly string _name;
		private readonly ITransport _transport;

	    private readonly Dictionary<string, PropertyNode> _children = new Dictionary<string, PropertyNode>();

	    public PropertyNode(ITransport transport, string name, object obj)
	    {
		    _obj = obj; // we don't change ourself; only our parent can do that
			// loop through all the properties and create children
			_name = name + ".";
			_transport = transport;
		    if (obj != null)
		    {
				var type = obj.GetType();
			    if (!FastMember.TypeHelpers._IsValueType(type) && type != typeof(string))
			    {
					_accessor = FastMember.TypeAccessor.Create(type);
				    foreach (var member in _accessor.GetMembers())
				    {
					    _children.Add(member.Name, new PropertyNode(_transport, _name + member.Name, _accessor[obj, member.Name]));
				    }
				    if (obj is INotifyPropertyChanged npc)
					    npc.PropertyChanged += OnPropertyChanged;
					_transport.Receive += OnReceivedValue;
				}
			}
		}

		public void Dispose()
		{
			if (_obj is INotifyPropertyChanged npc)
				npc.PropertyChanged -= OnPropertyChanged;
			_transport.Receive -= OnReceivedValue;
			lock(_children)
				foreach (var kvp in _children)
					kvp.Value.Dispose();
		}

		private void OnReceivedValue(string name, object value)
		{
			var fullName = _name + name;
			lock (_children)
			{
				_children[name].Dispose();
				_accessor[_obj, name] = value; // definitely dangerous to have this in the lock
				_children[name] = new PropertyNode(_transport, fullName, value);
			}
		}

		private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
	    {
			// this means that one of the properties on our child changed
			// that means we should ship the result and update our children nodes

			var fullName = _name + e.PropertyName;

			// update the children
			// the simplest plan is to blow away all the children and rebuild
			// we could do better than that by pushing an update through our tree
			// but it would be rare that we get a PropertyChanged event where the whole thing didn't change
			// hopefully they change their properties on the same thread every time
			object value;
			lock (_children)
			{
				value = _accessor[_obj, e.PropertyName]; // not sure this needs to be in the lock
				_children[e.PropertyName].Dispose();
				_children[e.PropertyName] = new PropertyNode(_transport, fullName, value);
			}
			
			 _transport.Send(fullName, value);
			
			// that line doesn't work unless we're on a leaf node? 
			// no; we can serialize and send the whole thing
			// surely one serialize call is better than none
		}
	}
}
