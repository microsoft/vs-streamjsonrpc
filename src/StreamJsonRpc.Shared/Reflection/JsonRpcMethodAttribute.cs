using System;
using Microsoft;

namespace StreamJsonRpc
{
    /// <summary>
    /// Attribute which allows a method to have a different name.  If applied on a method, the method's CLR name can no longer be used for remote invocation.
    /// </summary>
    /// <remarks>
    /// This attribute should be used when rpc message method names can be different from the actual CLR method names.
    /// Useful in cases where rpc message method names contain illegal characters for CLR method names, i.e. "text/OnDocumentChanged".
    /// 
    /// If methods are overloaded, each overload must define its own <see cref="JsonRpcMethodAttribute"/>  with all the same values.  
    /// Conflicts will result in error being thrown during <see cref="JsonRpc"/> construction.
    /// 
    /// If methods are overridden, the base class can define a <see cref="JsonRpcMethodAttribute"/> and derived classes will inherit the attribute.
    /// If derived class and base class have conflicting <see cref="JsonRpcMethodAttribute"/> values for a method, an error will be thrown during <see cref="JsonRpc"/> construction.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
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
