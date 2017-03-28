using System;
using Microsoft;

namespace StreamJsonRpc
{
    /// <summary>
    /// Attribute which allows a method to have an alternate name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class MethodNameAttribute : Attribute
    {
        /// <summary>
        /// Ctor.
        /// </summary>
        /// <param name="name">Alternate name of a method.</param>
        public MethodNameAttribute(string name)
        {
            Requires.NotNullOrWhiteSpace(name, nameof(name));

            Name = name;
        }

        /// <summary>
        /// Gets the alternate name of a method.
        /// </summary>
        public string Name { get; }
    }
}
