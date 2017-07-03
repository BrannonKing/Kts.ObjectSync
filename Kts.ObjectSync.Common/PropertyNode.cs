﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace Kts.ObjectSync.Common
{
	class PropertyNode: IDisposable
    {
	    private readonly object _value;
		private readonly FastMember.TypeAccessor _accessor;
		private readonly string _name;
		private readonly ITransport _transport;

	    private readonly Dictionary<string, PropertyNode> _children = new Dictionary<string, PropertyNode>();
	    public readonly Type PropertyType;
	    private readonly Func<string, bool> _shouldSend;
	    private readonly Func<string, bool> _shouldReceive;
	    private readonly Func<string, bool> _shouldSendOnConnected;

	    public PropertyNode(ITransport transport, ObjectForSynchronization ofs)
			:this(transport, ofs.ID, ofs, typeof(object), ofs.ShouldSend, ofs.ShouldReceive, ofs.ShouldSendOnConnected)
	    {
	    }

	    public PropertyNode(ITransport transport, string name, object value, Type propertyType, 
			Func<string, bool> shouldSend, Func<string, bool> shouldReceive, Func<string, bool> shouldSendOnConnected)
	    {
		    PropertyType = propertyType;
		    _value = value; // we don't change ourself; only our parent can do that
		    _shouldSend = shouldSend;
		    _shouldReceive = shouldReceive;
		    _shouldSendOnConnected = shouldSendOnConnected;
		    // loop through all the properties and create children
			_name = name + ".";
			_transport = transport;

		    if (value != null)
		    {
				var type = value.GetType();
			    if (!FastMember.TypeHelpers._IsValueType(type) && type != typeof(string))
			    {
					_accessor = FastMember.TypeAccessor.Create(type);
				    foreach (var member in _accessor.GetMembers())
				    {
					    var childValue = _accessor[value, member.Name];
						if (childValue is ObjectForSynchronization ofs)
							_children.Add(member.Name, new PropertyNode(_transport, ofs));
						else
							_children.Add(member.Name, new PropertyNode(_transport, _name + member.Name, 
								childValue, member.Type, _shouldSend, _shouldReceive, null));
				    }
				    if (value is INotifyPropertyChanged npc)
					    npc.PropertyChanged += OnPropertyChanged;
					_transport.Receive += OnReceivedValue;
				}
			}

		    if (shouldSendOnConnected != null)
		    {
			    _transport.Connected += OnConnected; // event should trigger on subscribe
		    }
		}

	    private void OnConnected(ITransport transport)
	    {
		    lock (_children)
		    {
			    foreach (var kvp in _children)
			    {
				    var fullName = _name + kvp.Key;
					if (_shouldSendOnConnected.Invoke(fullName))
				    {
					    if (_shouldSend != null && !_shouldSend.Invoke(fullName))
						    continue;
					    transport.Send(fullName, kvp.Value._value);
					}
				}
		    }
	    }

	    public void Dispose()
		{
			if (_value is INotifyPropertyChanged npc)
				npc.PropertyChanged -= OnPropertyChanged;
			_transport.Receive -= OnReceivedValue;
			_transport.Connected -= OnConnected;
			lock (_children)
				foreach (var kvp in _children)
					kvp.Value.Dispose();
		}

		private void OnReceivedValue(string name, object value)
		{
			if (!name.StartsWith(_name))
				return; // TODO: optimize this

			var childName = name.Substring(_name.Length);
			if (childName.Contains("."))
				return;

			if (_shouldReceive != null && !_shouldReceive.Invoke(name))
				return;

			lock (_children)
			{
				if (_children.TryGetValue(childName, out var node))
				{
					node.Dispose();
					if (node.PropertyType.IsInstanceOfType(value))
						_accessor[_value, childName] = value;
					else
						_accessor[_value, childName] = Convert.ChangeType(value, node.PropertyType);

					if (value is ObjectForSynchronization ofs)
						_children[childName] = new PropertyNode(_transport, ofs);
					else
						_children[childName] = new PropertyNode(_transport, name, value, node.PropertyType, _shouldSend, _shouldReceive, null);
				}
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
				value = _accessor[_value, e.PropertyName]; // not sure this needs to be in the lock
				if (_children.TryGetValue(e.PropertyName, out var node))
					node.Dispose();
				if (value is ObjectForSynchronization ofs)
					_children[e.PropertyName] = new PropertyNode(_transport, ofs);
				else
					_children[e.PropertyName] = new PropertyNode(_transport, fullName, value,
						node != null ? node.PropertyType : _accessor.GetMembers()[e.PropertyName].Type, 
						_shouldSend, _shouldReceive, null);
			}

			if (_shouldSend != null && !_shouldSend.Invoke(fullName))
			    return;
			 _transport.Send(fullName, value);
			
			// that line doesn't work unless we're on a leaf node? 
			// no; we can serialize and send the whole thing
			// surely one serialize call is better than none
		}
	}
}
