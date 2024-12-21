// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using Nerdbank.MessagePack;
using PolyType;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Protocol;

/// <summary>
/// The base class for a JSON-RPC request or response.
/// </summary>
[DataContract]
[KnownType(typeof(JsonRpcRequest))]
[KnownType(typeof(JsonRpcResult))]
[KnownType(typeof(JsonRpcError))]
#pragma warning disable CS0618 //'KnownSubTypeAttribute.KnownSubTypeAttribute(Type)' is obsolete: 'Use the generic version of this attribute instead.'
[KnownSubType(typeof(JsonRpcRequest))]
[KnownSubType(typeof(JsonRpcResult))]
[KnownSubType(typeof(JsonRpcError))]
#pragma warning restore CS0618
public abstract class JsonRpcMessage
{
    /// <summary>
    /// Gets or sets the version of the JSON-RPC protocol that this message conforms to.
    /// </summary>
    /// <value>Defaults to "2.0".</value>
    [DataMember(Name = "jsonrpc", Order = 0, IsRequired = true)]
    [STJ.JsonPropertyName("jsonrpc"), STJ.JsonPropertyOrder(0), STJ.JsonRequired]
    [PropertyShape(Name = "jsonrpc", Order = 0)]
    public string Version { get; set; } = "2.0";

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
    /// <exception cref="InvalidOperationException">May be thrown when called on an outbound message.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is reserved by the JSON-RPC spec.</exception>
    public virtual bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
    {
        Requires.NotNullOrEmpty(name, nameof(name));
        value = default;
        return false;
    }

    /// <summary>
    /// Sets a top-level property in the message that is an extension to JSON-RPC specification.
    /// </summary>
    /// <typeparam name="T">The type of value to be serialized.</typeparam>
    /// <param name="name">The name of the property. This should <em>not</em> collide with any property defined by the JSON-RPC specification.</param>
    /// <param name="value">The value for the property.</param>
    /// <returns>
    /// <see langword="true" /> if the formatter supports setting top-level properties;
    /// <see langword="false" /> otherwise.
    /// </returns>
    /// <exception cref="InvalidOperationException">May be thrown when called on an inbound message.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is reserved by the JSON-RPC spec.</exception>
    public virtual bool TrySetTopLevelProperty<T>(string name, [MaybeNull] T value)
    {
        Requires.NotNullOrEmpty(name, nameof(name));
        return false;
    }
}
