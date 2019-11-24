// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    /// <summary>
    /// An interface implemented by <see cref="IJsonRpcMessageFormatter"/> instances
    /// to support some formatter extensions such as <see cref="MessageFormatterEnumerableTracker"/>.
    /// </summary>
    public interface IJsonRpcFormatterState
    {
        /// <summary>
        /// Gets the id of the request or response currently being serialized.
        /// </summary>
        public RequestId SerializingMessageWithId { get; }

        /// <summary>
        /// Gets the ID of the response currently being deserialized.
        /// </summary>
        public RequestId DeserializingMessageWithId { get; }

        /// <summary>
        /// Gets a value indicating whether a <see cref="Protocol.JsonRpcRequest"/> is being serialized.
        /// </summary>
        /// <remarks>
        /// A response is being serialized if this property's value is <c>false</c> while <see cref="SerializingMessageWithId"/> is non-empty.
        /// </remarks>
        public bool SerializingRequest { get; }
    }
}
