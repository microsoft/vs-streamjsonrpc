// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.Serialization;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Describes the result of a successful method invocation.
    /// </summary>
    [DataContract]
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    public class JsonRpcResult : JsonRpcMessage, IJsonRpcMessageWithId
    {
        /// <summary>
        /// Gets or sets the value of the result of an invocation, if any.
        /// </summary>
        [DataMember(Name = "result", Order = 2, IsRequired = true, EmitDefaultValue = true)]
#pragma warning disable CA1721 // Property names should not match get methods
        public object? Result { get; set; }
#pragma warning restore CA1721 // Property names should not match get methods

        /// <summary>
        /// Gets or sets the declared type of the return value.
        /// </summary>
        /// <remarks>
        /// This value is not serialized, but is used by the RPC server to assist in serialization where necessary.
        /// </remarks>
        [IgnoreDataMember]
        public Type? ResultDeclaredType { get; set; }

        /// <summary>
        /// Gets or sets an identifier established by the client if a response to the request is expected.
        /// </summary>
        /// <value>A <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/>, or <c>null</c>.</value>
        [Obsolete("Use " + nameof(RequestId) + " instead.")]
        [IgnoreDataMember]
        public object? Id
        {
            get => this.RequestId.ObjectValue;
            set => this.RequestId = RequestId.Parse(value);
        }

        /// <summary>
        /// Gets or sets an identifier established by the client if a response to the request is expected.
        /// </summary>
        [DataMember(Name = "id", Order = 1, IsRequired = true)]
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
            return new JObject
            {
                new JProperty("id", this.RequestId.ObjectValue),
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
}
