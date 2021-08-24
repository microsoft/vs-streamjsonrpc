// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Collections.ObjectModel;
    using System.Runtime.Serialization;
    using Microsoft;

    /// <summary>
    /// The base class for a JSON-RPC request or response.
    /// </summary>
    [DataContract]
    [KnownType(typeof(JsonRpcRequest))]
    [KnownType(typeof(JsonRpcResult))]
    [KnownType(typeof(JsonRpcError))]
    public abstract class JsonRpcMessage
    {
        private Dictionary<string, TypedValue>? extraTopLevelProperties;
        private ReadOnlyDictionary<string, TypedValue>? readOnlyExtraTopLevelProperties;

        /// <summary>
        /// Gets or sets the version of the JSON-RPC protocol that this message conforms to.
        /// </summary>
        /// <value>Defaults to "2.0".</value>
        [DataMember(Name = "jsonrpc", Order = 0, IsRequired = true)]
        public string Version { get; set; } = "2.0";

        /// <summary>
        /// Gets a map of the extra top-level properties set with <see cref="SetTopLevelProperty{T}(string, T)"/>.
        /// </summary>
        /// <remarks>
        /// This map should not be used on incoming messages.
        /// Use <see cref="TryGetTopLevelProperty{T}(string, out T)"/> to read such extra top-level properties.
        /// </remarks>
        public IReadOnlyDictionary<string, TypedValue> OutboundExtraTopLevelProperties
        {
            get
            {
                if (this.readOnlyExtraTopLevelProperties is null)
                {
                    if (this.extraTopLevelProperties?.Count > 0)
                    {
                        this.readOnlyExtraTopLevelProperties = new ReadOnlyDictionary<string, TypedValue>(this.extraTopLevelProperties);
                    }
                    else
                    {
                        return ImmutableDictionary<string, TypedValue>.Empty;
                    }
                }

                return this.readOnlyExtraTopLevelProperties;
            }
        }

        /// <summary>
        /// Retrieves a top-level property from an incoming message that is an extension to the JSON-RPC specification.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the value as, if it is present.</typeparam>
        /// <param name="name">The name of the top-level property.</param>
        /// <param name="value">
        /// Receives the deserialized value if the <see cref="IJsonRpcMessageFormatter"/> supports reading such properties
        /// and the property is present in the message.
        /// Otherwise, this parameter is set to its <see langword="default" /> value.
        /// </param>
        /// <returns>
        /// <see langword="true" /> if the <see cref="IJsonRpcMessageFormatter"/> supports this extensibility
        /// and the property was present on the message; otherwise <see langword="false" />.
        /// </returns>
        public virtual bool TryGetTopLevelProperty<T>(string name, out T? value)
        {
            value = default;
            return false;
        }

        /// <summary>
        /// Sets a top-level property in the message that is an extension to JSON-RPC specification.
        /// </summary>
        /// <typeparam name="T">The type of value to be serialized.</typeparam>
        /// <param name="name">The name of the property. This should <em>not</em> collide with any property defined by the JSON-RPC specification.</param>
        /// <param name="value">The value for the property.</param>
        public void SetTopLevelProperty<T>(string name, T? value)
        {
            Requires.NotNull(name, nameof(name));
            this.extraTopLevelProperties ??= new(StringComparer.Ordinal);
            this.extraTopLevelProperties[name] = new TypedValue(value, typeof(T));
        }

        /// <summary>
        /// A value and its declared type.
        /// </summary>
        public struct TypedValue
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TypedValue"/> struct.
            /// </summary>
            /// <param name="value">The value.</param>
            /// <param name="valueType">The declared type of the value.</param>
            public TypedValue(object? value, Type valueType)
            {
                this.Value = value;
                this.ValueType = valueType;
            }

            /// <summary>
            /// Gets the value.
            /// </summary>
            public object? Value { get; }

            /// <summary>
            /// Gets the declared type of the value.
            /// </summary>
            /// <remarks>
            /// This may not be the concrete type of <see cref="Value"/>, but is the type that the receiving side is expecting to deserialize.
            /// When union types are involved, the declared type may be critical to successful deserialization, even if the concrete type is derived from this.
            /// </remarks>
            public Type? ValueType { get; }
        }
    }
}
