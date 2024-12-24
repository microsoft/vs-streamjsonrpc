// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.Serialization;
using PolyType;
using StreamJsonRpc.Reflection;
using JsonNET = Newtonsoft.Json.Linq;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Protocol;

/// <summary>
/// Describes the error resulting from a <see cref="JsonRpcRequest"/> that failed on the server.
/// </summary>
[DataContract]
[GenerateShape]
[DebuggerDisplay("{" + nameof(DebuggerDisplay) + "}")]
public partial class JsonRpcError : JsonRpcMessage, IJsonRpcMessageWithId
{
    /// <summary>
    /// Gets or sets the detail about the error.
    /// </summary>
    [DataMember(Name = "error", Order = 2, IsRequired = true)]
    [STJ.JsonPropertyName("error"), STJ.JsonPropertyOrder(2), STJ.JsonRequired]
    [PropertyShape(Name = "error", Order = 2)]
    public ErrorDetail? Error { get; set; }

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
    [DataMember(Name = "id", Order = 1, IsRequired = true, EmitDefaultValue = true)]
    [STJ.JsonPropertyName("id"), STJ.JsonPropertyOrder(1), STJ.JsonRequired]
    [PropertyShape(Name = "id", Order = 1)]
    public RequestId RequestId { get; set; }

    /// <summary>
    /// Gets the string to display in the debugger for this instance.
    /// </summary>
    protected string DebuggerDisplay => $"Error: {this.Error?.Code} \"{this.Error?.Message}\" ({this.RequestId})";

    /// <inheritdoc/>
    public override string ToString()
    {
        return new JsonNET.JObject
        {
            new JsonNET.JProperty("id", this.RequestId.ObjectValue),
            new JsonNET.JProperty("error", new JsonNET.JObject
            {
                new JsonNET.JProperty("code", this.Error?.Code),
                new JsonNET.JProperty("message", this.Error?.Message),
            }),
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// Describes the error.
    /// </summary>
    [DataContract]
    [GenerateShape]
    public partial class ErrorDetail
    {
        /// <summary>
        /// Gets or sets a number that indicates the error type that occurred.
        /// </summary>
        /// <value>
        /// The error codes from and including -32768 to -32000 are reserved for errors defined by the spec or this library.
        /// Codes outside that range are available for app-specific error codes.
        /// </value>
        [DataMember(Name = "code", Order = 0, IsRequired = true)]
        [STJ.JsonPropertyName("code"), STJ.JsonPropertyOrder(0), STJ.JsonRequired]
        [PropertyShape(Name = "code", Order = 0)]
        public JsonRpcErrorCode Code { get; set; }

        /// <summary>
        /// Gets or sets a short description of the error.
        /// </summary>
        /// <remarks>
        /// The message SHOULD be limited to a concise single sentence.
        /// </remarks>
        [DataMember(Name = "message", Order = 1, IsRequired = true)]
        [STJ.JsonPropertyName("message"), STJ.JsonPropertyOrder(1), STJ.JsonRequired]
        [PropertyShape(Name = "message", Order = 1)]
        public string? Message { get; set; }

        /// <summary>
        /// Gets or sets additional data about the error.
        /// </summary>
        [DataMember(Name = "data", Order = 2, IsRequired = false)]
        [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        [STJ.JsonPropertyName("data"), STJ.JsonPropertyOrder(2)]
        [PropertyShape(Name = "data", Order = 2)]
        public object? Data { get; set; }

        /// <summary>
        /// Gets the value of the <see cref="Data"/>, taking into account any possible type coercion.
        /// </summary>
        /// <typeparam name="T">The <see cref="Type"/> to coerce the <see cref="Data"/> to.</typeparam>
        /// <returns>The result.</returns>
        public T GetData<T>() => (T)this.GetData(typeof(T))!;

        /// <summary>
        /// Gets the value of the <see cref="Data"/>, taking into account any possible type coercion.
        /// </summary>
        /// <param name="dataType">The <see cref="Type"/> to deserialize the data object to.</param>
        /// <returns>The result.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="dataType"/> is null.</exception>
        /// <remarks>
        /// Derived types may override this method in order to deserialize the <see cref="Data"/>
        /// such that it can be assignable to <paramref name="dataType"/>.
        /// The default implementation does nothing to convert the <see cref="Data"/> object to match <paramref name="dataType"/>, but simply returns the existing object.
        /// Derived types should *not* throw exceptions. This is a best effort method and returning null or some other value is preferable to throwing
        /// as it can disrupt an existing exception handling path.
        /// </remarks>
        public virtual object? GetData(Type dataType)
        {
            Requires.NotNull(dataType, nameof(dataType));
            return this.Data;
        }

        /// <summary>
        /// Provides a hint for a deferred deserialization of the <see cref="Data"/> value as to the type
        /// argument that will be used when calling <see cref="GetData{T}"/> later.
        /// </summary>
        /// <param name="dataType">The type that will be used as the generic type argument to <see cref="GetData{T}"/>.</param>
        /// <remarks>
        /// Overriding methods in types that retain buffers used to deserialize should deserialize within this method and clear those buffers
        /// to prevent further access to these buffers which may otherwise happen concurrently with a call to <see cref="IJsonRpcMessageBufferManager.DeserializationComplete"/>
        /// that would recycle the same buffer being deserialized from.
        /// </remarks>
        protected internal virtual void SetExpectedDataType(Type dataType)
        {
        }
    }
}
