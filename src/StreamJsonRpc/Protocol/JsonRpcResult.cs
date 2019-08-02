// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Diagnostics;
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
        [DataMember(Name = "result", Order = 1, IsRequired = true, EmitDefaultValue = true)]
        public object Result { get; set; }

        /// <summary>
        /// Gets or sets an identifier established by the client if a response to the request is expected.
        /// </summary>
        /// <value>A <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/>, or <c>null</c>.</value>
        [DataMember(Name = "id", Order = 2, IsRequired = true)]
        public object Id { get; set; }

        /// <summary>
        /// Gets the string to display in the debugger for this instance.
        /// </summary>
        protected string DebuggerDisplay => $"Result: {this.Result} ({this.Id})";

        /// <summary>
        /// Gets the value of the <see cref="Result"/>, taking into account any possible type coercion.
        /// </summary>
        /// <typeparam name="T">The <see cref="Type"/> to coerce the <see cref="Result"/> to.</typeparam>
        /// <returns>The result.</returns>
        /// <remarks>
        /// Derived types may override this method in order to deserialize the <see cref="Result"/>
        /// such that it can be assignable to <typeparamref name="T"/>.
        /// </remarks>
        public virtual T GetResult<T>() => (T)this.Result;

        /// <inheritdoc/>
        public override string ToString()
        {
            return new JObject
            {
                new JProperty("id", this.Id),
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
