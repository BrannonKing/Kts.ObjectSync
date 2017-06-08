using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text;

namespace Kts.ObjectSync
{
    class PropertyNode
    {
	    private readonly object _obj;

	    private readonly ConcurrentDictionary<string, PropertyNode> _children = new ConcurrentDictionary<string, PropertyNode>();

	    private PropertyNode(object obj, Func<)
	    {
		    _obj = obj; // we don't change ourself; only our parent can do that
		    // loop through all the properties and create children
		    if (obj != null)
		    {
			    var type = obj.GetType();
			    if (!FastMember.TypeHelpers._IsValueType(type))
			    {
				    var accessor = FastMember.TypeAccessor.Create(type);
				    foreach (var member in accessor.GetMembers())
				    {
					    _children.TryAdd(member.Name, new PropertyNode(accessor[obj, member.Name]));
				    }
				    if (obj is INotifyPropertyChanged npc)
					    npc.PropertyChanged += OnPropertyChanged;
			    }
			}
	    }

	    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
	    {
		    // we can store a running list of names and the main sender
			// or we can run this up the tree
	    }
    }
}
