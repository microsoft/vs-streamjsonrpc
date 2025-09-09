#if !POLYTYPE

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1403 // File may only contain a single namespace
#pragma warning disable SA1602 // Enumeration items must be documented
#pragma warning disable SA1649 // File name must match first type name
#pragma warning disable CS9113 // Parameter 'x' is unread.

using System.Diagnostics;

namespace PolyType
{
    internal enum MethodShapeFlags
    {
        PublicInstance,
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    internal class GenerateShapeAttribute : Attribute
    {
        public MethodShapeFlags IncludeMethods { get; set; }
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    internal class TypeShapeAttribute : Attribute
    {
        public MethodShapeFlags IncludeMethods { get; set; }
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    internal class GenerateShapeForAttribute<T> : Attribute
    {
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class PropertyShapeAttribute : Attribute
    {
        public string? Name { get; set; }

        public int Order { get; set; }

        public bool Ignore { get; set; }

        public bool IsRequired { get; set; }
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Method)]
    internal class MethodShapeAttribute : Attribute
    {
        public string? Name { get; set; }

        public bool Ignore { get; set; }
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Constructor)]
    internal class ConstructorShapeAttribute : Attribute
    {
    }

    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    internal class DerivedTypeShapeAttribute(Type type) : Attribute
    {
        public int Tag { get; set; }
    }
}

namespace Nerdbank.MessagePack
{
    [Conditional("NEVER")]
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class KeyAttribute : Attribute
    {
        public KeyAttribute(int key)
        {
            this.Key = key;
        }

        public int Key { get; }
    }
}

#endif
