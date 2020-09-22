// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Microsoft;

    /// <summary>
    /// Attribute which changes the name by which this method can be invoked over JSON-RPC.
    /// If applied on a method, the method's CLR name can no longer be used for remote invocation.
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
        public JsonRpcMethodAttribute(string? name)
        {
#pragma warning disable CA1820 // Test for empty strings using string length
            if (name == string.Empty)
#pragma warning restore CA1820 // Test for empty strings using string length
            {
                // We actually allow null, but we don't want to see empty strings.
                Requires.NotNullOrEmpty(name, nameof(name));
            }

            this.Name = name;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcMethodAttribute"/> class.
        /// </summary>
        public JsonRpcMethodAttribute()
        {
        }

        /// <summary>
        /// Gets the public RPC name by which this method will be invoked.
        /// </summary>
        /// <value>May be <c>null</c> if the method's name has not been overridden.</value>
        public string? Name { get; }

        /// <summary>
        /// Gets or sets a value indicating whether JSON-RPC named arguments should all be deserialized into this method's first parameter.
        /// </summary>
        public bool UseSingleObjectParameterDeserialization { get; set;  }
    }
}
