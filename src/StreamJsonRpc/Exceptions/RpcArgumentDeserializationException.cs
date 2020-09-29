// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Globalization;
    using System.Runtime.Serialization;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An exception thrown from <see cref="JsonRpcRequest.TryGetArgumentByNameOrIndex(string?, int, Type?, out object?)"/>
    /// when the argument cannot be deserialized to the requested type, typically due to an incompatibility or exception thrown from the deserializer.
    /// </summary>
    [Serializable]
#pragma warning disable CA1032 // Implement standard exception constructors
    public class RpcArgumentDeserializationException : RemoteRpcException
#pragma warning restore CA1032 // Implement standard exception constructors
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RpcArgumentDeserializationException"/> class.
        /// </summary>
        /// <param name="argumentName">The name of the argument from the JSON-RPC request that failed to deserialize, if available.</param>
        /// <param name="argumentPosition">The 0-based index of the argument from the JSON-RPC request that failed to deserialize, if available.</param>
        /// <param name="deserializedType">The <see cref="Type"/> to which deserialization of the argument was attempted.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        public RpcArgumentDeserializationException(string? argumentName, int? argumentPosition, Type? deserializedType, Exception? innerException)
            : this(string.Format(CultureInfo.CurrentCulture, Resources.FailureDeserializingRpcArgument, argumentName, argumentPosition, deserializedType, innerException?.Message), innerException)
        {
            this.ArgumentName = argumentName;
            this.ArgumentPosition = argumentPosition;
            this.DeserializedType = deserializedType;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RpcArgumentDeserializationException"/> class.
        /// </summary>
        /// <inheritdoc cref="RpcArgumentDeserializationException(string, Exception)"/>
        public RpcArgumentDeserializationException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RpcArgumentDeserializationException"/> class.
        /// </summary>
        /// <inheritdoc cref="RemoteRpcException(string, Exception)"/>
        public RpcArgumentDeserializationException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RpcArgumentDeserializationException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Serialization context.</param>
        protected RpcArgumentDeserializationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.ArgumentName = info.GetString(nameof(this.ArgumentName));
            this.ArgumentPosition = info.GetInt32(nameof(this.ArgumentPosition));
            if (this.ArgumentPosition == -1)
            {
                this.ArgumentPosition = null;
            }
        }

        /// <summary>
        /// Gets the name of the argument from the JSON-RPC request that failed to deserialize, if available.
        /// </summary>
        /// <remarks>
        /// This value will be <c>null</c> when the JSON-RPC request uses positional arguments.
        /// </remarks>
        public string? ArgumentName { get; private set; }

        /// <summary>
        /// Gets the 0-based index of the argument from the JSON-RPC request that failed to deserialize, if available.
        /// </summary>
        /// <remarks>
        /// This value will be <c>null</c> when the JSON-RPC request uses named arguments.
        /// </remarks>
        public int? ArgumentPosition { get; private set; }

        /// <summary>
        /// Gets the <see cref="Type"/> to which deserialization of the argument was attempted.
        /// </summary>
        public Type? DeserializedType { get; private set; }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(this.ArgumentName), this.ArgumentName);
            info.AddValue(nameof(this.ArgumentPosition), this.ArgumentPosition ?? -1);
        }
    }
}
