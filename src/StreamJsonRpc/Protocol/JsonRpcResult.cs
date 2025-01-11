// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.Serialization;
using Nerdbank.MessagePack;
using PolyType;
using JsonNET = Newtonsoft.Json.Linq;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Protocol;

/// <summary>
/// Describes the result of a successful method invocation.
/// </summary>
[DataContract]
[GenerateShape]
[MessagePackConverter(typeof(NerdbankMessagePackFormatter.JsonRpcResultConverter))]
[DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
public partial class JsonRpcResult : JsonRpcMessage, IJsonRpcMessageWithId
{
    /// <summary>
    /// Gets or sets the value of the result of an invocation, if any.
    /// </summary>
    [DataMember(Name = "result", Order = 2, IsRequired = true, EmitDefaultValue = true)]
    [STJ.JsonPropertyName("result"), STJ.JsonPropertyOrder(2), STJ.JsonRequired]
    [PropertyShape(Name = "result", Order = 2)]
    public object? Result { get; set; }

    /// <summary>
    /// Gets or sets the declared type of the return value.
    /// </summary>
    /// <remarks>
    /// This value is not serialized, but is used by the RPC server to assist in serialization where necessary.
    /// </remarks>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public Type? ResultDeclaredType { get; set; }

    /// <summary>
    /// Gets or sets an identifier established by the client if a response to the request is expected.
    /// </summary>
    /// <value>A <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/>, or <see langword="null"/>.</value>
    [Obsolete("Use " + nameof(RequestId) + " instead.")]
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public object? Id
    {
        get => this.RequestId.ObjectValue;
        set => this.RequestId = RequestId.Parse(value);
    }

    /// <summary>
    /// Gets or sets an identifier established by the client if a response to the request is expected.
    /// </summary>
    [DataMember(Name = "id", Order = 1, IsRequired = true)]
    [STJ.JsonPropertyName("id"), STJ.JsonPropertyOrder(1), STJ.JsonRequired]
    [PropertyShape(Name = "id", Order = 1)]
    public RequestId RequestId { get; set; }

    /// <summary>
    /// Gets the string to display in the debugger for this instance.
    /// </summary>
    protected string DebuggerDisplay => $"Result: {this.Result} ({this.RequestId})";

    /// <summary>
    /// Gets the value of the <see cref="Result"/>, taking into account any possible type coercion.
    /// </summary>
    /// <typeparam name="T">The <see cref="Type"/> to coerce the <see cref="Result"/> to.</typeparam>
    /// <returns>The result.</returns>
    /// <remarks>
    /// Derived types may override this method in order to deserialize the <see cref="Result"/>
    /// such that it can be assignable to <typeparamref name="T"/>.
    /// </remarks>
    public virtual T GetResult<T>() => (T)this.Result!;

    /// <inheritdoc/>
    public override string ToString()
    {
        return new JsonNET.JObject
        {
            new JsonNET.JProperty("id", this.RequestId.ObjectValue),
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// Provides a hint for a deferred deserialization of the <see cref="Result"/> value as to the type
    /// argument that will be used when calling <see cref="GetResult{T}"/> later.
    /// </summary>
    /// <param name="resultType">The type that will be used as the generic type argument to <see cref="GetResult{T}"/>.</param>
    protected internal virtual void SetExpectedResultType(Type resultType)
    {
    }
}
