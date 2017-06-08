using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FastMember
{
	/// <summary>
	/// Represents an abstracted view of the members defined for a type
	/// </summary>
	public sealed class MemberSet : IReadOnlyList<Member>
	{
		readonly Member[] members;
		internal MemberSet(Type type)
		{
			const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
			members = type.GetProperties(PublicInstance).Cast<MemberInfo>().Concat(type.GetFields(PublicInstance)).OrderBy(x => x.Name)
				.Select(member => new Member(member)).ToArray();
		}
		/// <summary>
		/// Return a sequence of all defined members
		/// </summary>
		public IEnumerator<Member> GetEnumerator()
		{
			foreach (var member in members) yield return member;
		}
		/// <summary>
		/// Get a member by index
		/// </summary>
		public Member this[int index] => members[index];

		/// <summary>
		/// The number of members defined for this type
		/// </summary>
		public int Count => members.Length;

		Member IReadOnlyList<Member>.this[int index] => members[index];

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }

	}
	/// <summary>
	/// Represents an abstracted view of an individual member defined for a type
	/// </summary>
	public sealed class Member
	{
		private readonly MemberInfo member;
		internal Member(MemberInfo member)
		{
			this.member = member;
		}
		/// <summary>
		/// The name of this member
		/// </summary>
		public string Name => member.Name;

		/// <summary>
		/// The type of value stored in this member
		/// </summary>
		public Type Type
		{
			get
			{
				if (member is FieldInfo f) return f.FieldType;
				if (member is PropertyInfo p) return p.PropertyType;
				throw new NotSupportedException(member.GetType().Name);
			}
		}

		/// <summary>
		/// Is the attribute specified defined on this type
		/// </summary>
		public bool IsDefined(Type attributeType)
		{
			if (attributeType == null) throw new ArgumentNullException(nameof(attributeType));
#if COREFX
			foreach (var attrib in member.CustomAttributes)
			{
				if (attrib.AttributeType == attributeType) return true;
			}
			return false;
#else
            return Attribute.IsDefined(member, attributeType);
#endif
		}

		/// <summary>
		/// Getting Attribute Type
		/// </summary>
		public Attribute GetAttribute(Type attributeType, bool inherit)
		{
#if COREFX
			return attributeType.GetTypeInfo().GetCustomAttribute(attributeType, inherit);
#else
			return Attribute.GetCustomAttribute(member, attributeType, inherit);
#endif
		}

		/// <summary>
		/// Property Can Write
		/// </summary>
		public bool CanWrite
		{
			get
			{
				if (member is PropertyInfo m)
					return m.CanWrite;
				throw new NotSupportedException(member.ToString());
			}
		}

		/// <summary>
		/// Property Can Read
		/// </summary>
		public bool CanRead
		{
			get
			{
				if (member is PropertyInfo m)
					return m.CanRead;
				throw new NotSupportedException(member.ToString());
			}
		}
	}
}
