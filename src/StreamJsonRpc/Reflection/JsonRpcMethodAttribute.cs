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
        /// <param name="useSingleObjectParameterDeserialization">Indicates whether the method supports deserialization for single object parameter.</param>
        public JsonRpcMethodAttribute(string name, bool useSingleObjectParameterDeserialization = false)
        {
            Requires.NotNullOrWhiteSpace(name, nameof(name));

            this.Name = name;
            this.UseSingleObjectParameterDeserialization = useSingleObjectParameterDeserialization;
        }

        /// <summary>
        /// Gets the replacement name of a method.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets a value indicating whether the method supports deserialization for sinlge object parameter.
        /// </summary>
        public bool UseSingleObjectParameterDeserialization { get; }
    }
}
