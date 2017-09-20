using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Kts.ObjectSync.Common
{
	class PropertyNode: IDisposable
    {
	    private readonly object _value;
		private readonly FastMember.TypeAccessor _accessor;
		private readonly string _name;
	    internal readonly string Name;
		private readonly ITransport _transport;
		private readonly List<string> _blocked = new List<string>();

	    private readonly ConcurrentDictionary<string, PropertyNode> _children;
	    public readonly Type PropertyType; 
	    private readonly Func<string, bool> _shouldSend;
	    private readonly Func<string, bool> _shouldReceive;

	    public PropertyNode(ITransport transport, ObjectForSynchronization ofs)
			:this(transport, ofs.ID, ofs, typeof(object), ofs.ShouldSend, ofs.ShouldReceive)
	    {
	    }

	    public PropertyNode(ITransport transport, string name, object value, Type propertyType, 
			Func<string, bool> shouldSend, Func<string, bool> shouldReceive)
	    {
		    PropertyType = propertyType;
		    _shouldSend = shouldSend;
		    _shouldReceive = shouldReceive;
		    Name = name;
			_transport = transport;

		    if (value != null)
		    {
				if (!propertyType.IsValueType && propertyType != typeof(string))
			    {
				    _name = name + ".";
					_value = value; // we don't change ourself; only our parent can do that
					_accessor = FastMember.TypeAccessor.Create(value.GetType());
					_children = new ConcurrentDictionary<string, PropertyNode>();
				    if (_accessor.GetMembersSupported)
				    {
					    foreach (var member in _accessor.GetMembers())
					    {
						    var childValue = _accessor[value, member.Name];
						    PropertyNode node;
						    if (childValue is ObjectForSynchronization ofs)
							    node = new PropertyNode(_transport, ofs);
						    else
							    node = new PropertyNode(_transport, _name + member.Name,
								    childValue, member.Type, _shouldSend, _shouldReceive);
						    _children[member.Name] = node;
						    _transport.RegisterReceiver(node.Name, node.PropertyType, OnReceivedValue);
						}
				    }
				    else if (value is IDynamicMetaObjectProvider dmop)
				    {
					    var names = dmop.GetMetaObject(Expression.Constant(value)).GetDynamicMemberNames();
					    foreach (var childName in names)
					    {
						    var childValue = _accessor[value, childName];
						    PropertyNode node;
						    if (childValue is ObjectForSynchronization ofs)
							    node = new PropertyNode(_transport, ofs);
						    else
							    node = new PropertyNode(_transport, _name + childName,
								    childValue, childValue?.GetType() ?? typeof(object), _shouldSend, _shouldReceive);
						    _children[childName] = node;
							_transport.RegisterReceiver(node.Name, node.PropertyType, OnReceivedValue);
					    }
					}
				    if (value is INotifyPropertyChanged npc)
					    npc.PropertyChanged += OnPropertyChanged;
				}
			}
		}

	    public void Dispose()
		{
			if (_value is INotifyPropertyChanged npc)
				npc.PropertyChanged -= OnPropertyChanged;
			if (_children != null)
				foreach (var kvp in _children)
				{
					_transport.UnregisterReceiver(kvp.Value.Name);
					kvp.Value.Dispose();
				}
		}

		private void OnReceivedValue(string name, object value)
		{
			System.Diagnostics.Debug.Assert(name.StartsWith(_name));
			if (_shouldReceive != null && !_shouldReceive.Invoke(name))
				return;

			var childName = name.Substring(_name.Length);
			System.Diagnostics.Debug.Assert(!childName.Contains("."));
			lock (_blocked) _blocked.Add(childName);
			_accessor[_value, childName] = value;
			lock (_blocked) _blocked.Remove(childName);
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
		    var childName = e.PropertyName;
			var value = _accessor[_value, childName];

			// we can't allow the client to set this while the server is setting it and vice-versa

		    var node = RebuildNode(fullName, childName, value);
		    lock (_blocked)
			    if (_blocked.Contains(childName))
				    return;
		    if (_shouldSend != null && !_shouldSend.Invoke(fullName))
			    return;
			 _transport.Send(fullName, node.PropertyType, value);
			
			// that line doesn't work unless we're on a leaf node? 
			// no; we can serialize and send the whole thing
			// surely one serialize call is better than none
		}

	    private PropertyNode RebuildNode(string fullName, string childName, object value)
	    {
		    PropertyNode node;
		    _children.TryGetValue(childName, out node);
			if (node == null || node._name != null)
		    {
			    node?.Dispose();
			    if (value is ObjectForSynchronization ofs)
				    node = new PropertyNode(_transport, ofs);
			    else
				    node = new PropertyNode(_transport, fullName, value,
					    node != null ? node.PropertyType : _accessor.GetMembers().First(m => m.Name == childName).Type,
					    _shouldSend, _shouldReceive);
			    _children[childName] = node;
		    }
		    return node;
	    }
    }
}
