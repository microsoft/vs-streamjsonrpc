// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.Serialization;
    using Microsoft;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Describes the error resulting from a <see cref="JsonRpcRequest"/> that failed on the server.
    /// </summary>
    [DataContract]
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + "}")]
    public class JsonRpcError : JsonRpcMessage, IJsonRpcMessageWithId
    {
        /// <summary>
        /// Gets or sets the detail about the error.
        /// </summary>
        [DataMember(Name = "error", Order = 2, IsRequired = true)]
        public ErrorDetail? Error { get; set; }

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
        [DataMember(Name = "id", Order = 1, IsRequired = true, EmitDefaultValue = true)]
        public RequestId RequestId { get; set; }

        /// <summary>
        /// Gets the string to display in the debugger for this instance.
        /// </summary>
        protected string DebuggerDisplay => $"Error: {this.Error?.Code} \"{this.Error?.Message}\" ({this.RequestId})";

        /// <inheritdoc/>
        public override string ToString()
        {
            return new JObject
            {
                new JProperty("id", this.RequestId.ObjectValue),
                new JProperty("error", new JObject
                {
                    new JProperty("code", this.Error?.Code),
                    new JProperty("message", this.Error?.Message),
                }),
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Describes the error.
        /// </summary>
        [DataContract]
        public class ErrorDetail
        {
            /// <summary>
            /// Gets or sets a number that indicates the error type that occurred.
            /// </summary>
            /// <value>
            /// The error codes from and including -32768 to -32000 are reserved for errors defined by the spec or this library.
            /// Codes outside that range are available for app-specific error codes.
            /// </value>
            [DataMember(Name = "code", Order = 0, IsRequired = true)]
            public JsonRpcErrorCode Code { get; set; }

            /// <summary>
            /// Gets or sets a short description of the error.
            /// </summary>
            /// <remarks>
            /// The message SHOULD be limited to a concise single sentence.
            /// </remarks>
            [DataMember(Name = "message", Order = 1, IsRequired = true)]
            public string? Message { get; set; }

            /// <summary>
            /// Gets or sets additional data about the error.
            /// </summary>
            [DataMember(Name = "data", Order = 2, IsRequired = false)]
            [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
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
            protected internal virtual void SetExpectedDataType(Type dataType)
            {
            }
        }
    }
}
