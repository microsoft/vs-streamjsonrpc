using System;
using Microsoft;

namespace StreamJsonRpc
{
    /// <summary>
    /// Attribute which allows a method to have a replacement name.
    /// </summary>
    /// <remarks>
    /// This attribute should be used when rpc message method names are different from the actual C# method names.
    /// Useful in cases where rpc message method names contain illegal characters for C# method names, i.e. "text/OnDocumentChanged".
    /// 
    /// If methods are overloaded, each overload must define its own JsonRpcMethodAttribute with all the same values.  
    /// Conflicts will result in error being thrown during method invocation.
    /// 
    /// If methods are overridden, the base class can define a JsonRpcMethodAttribute and derived classes will inherit the attribute.
    /// If derived class and base class have conflicting JsonRpcMethodAttribute values for a method, an error will be thrown during method invocation.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class JsonRpcMethodAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcMethodAttribute" /> class.
        /// </summary>
        /// <param name="name">Replacement name of a method.</param>
        public JsonRpcMethodAttribute(string name)
        {
            Requires.NotNullOrWhiteSpace(name, nameof(name));

            this.Name = name;
        }

        /// <summary>
        /// Gets the replacement name of a method.
        /// </summary>
        public string Name { get; }
    }
}
