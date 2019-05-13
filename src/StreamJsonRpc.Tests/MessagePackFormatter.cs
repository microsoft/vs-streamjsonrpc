// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using MessagePack;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
    /// </summary>
    /// <remarks>
    /// The MessagePack implementation used here comes from https://github.com/neuecc/MessagePack-CSharp.
    /// The README on that project site describes use cases and its performance compared to alternative
    /// .NET MessagePack implementations and this one appears to be the best by far.
    /// </remarks>
    public class MessagePackFormatter : IJsonRpcMessageFormatter
    {
        /// <summary>
        /// A value indicating whether to use LZ4 compression.
        /// </summary>
        private readonly bool compress;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class
        /// with LZ4 compression.
        /// </summary>
        public MessagePackFormatter()
            : this(compress: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        /// <param name="compress">A value indicating whether to use LZ4 compression.</param>
        public MessagePackFormatter(bool compress)
        {
            this.compress = compress;
        }

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            return this.compress
                ? (JsonRpcMessage)LZ4MessagePackSerializer.Typeless.Deserialize(contentBuffer.AsStream())
                : (JsonRpcMessage)MessagePackSerializer.Typeless.Deserialize(contentBuffer.AsStream());
        }

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            if (message is JsonRpcRequest request && request.Arguments != null && request.ArgumentsList == null && !(request.Arguments is IReadOnlyDictionary<string, object>))
            {
                // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
                // the parameters can be matched to the method we're invoking.
                request.Arguments = GetParamsObjectDictionary(request.Arguments);
            }

            if (this.compress)
            {
                LZ4MessagePackSerializer.Typeless.Serialize(contentBuffer.AsStream(), message);
            }
            else
            {
                MessagePackSerializer.Typeless.Serialize(contentBuffer.AsStream(), message);
            }
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => MessagePackSerializer.ToJson<object>(message);

        /// <summary>
        /// Extracts a dictionary of property names and values from the specified params object.
        /// </summary>
        /// <param name="paramsObject">The params object.</param>
        /// <returns>A dictionary, or <c>null</c> if <paramref name="paramsObject"/> is null.</returns>
        /// <remarks>
        /// In its present implementation, this method disregards any renaming attributes that would give
        /// the properties on the parameter object a different name. The <see cref="MessagePackSerializer"/>
        /// doesn't expose a simple way of doing this, so we'd have to emulate it by supporting both
        /// <see cref="DataMemberAttribute.Name"/> and <see cref="KeyAttribute.StringKey"/> handling.
        /// </remarks>
        private static Dictionary<string, object> GetParamsObjectDictionary(object paramsObject)
        {
            if (paramsObject == null)
            {
                return null;
            }

            if (paramsObject is IReadOnlyDictionary<object, object> dictionary)
            {
                // Anonymous types are serialized this way.
                return dictionary.ToDictionary(kv => (string)kv.Key, kv => kv.Value);
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal);

            const BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
            foreach (var property in paramsObject.GetType().GetTypeInfo().GetProperties(bindingFlags))
            {
                if (property.GetMethod != null)
                {
                    result[property.Name] = property.GetValue(paramsObject);
                }
            }

            foreach (var field in paramsObject.GetType().GetTypeInfo().GetFields(bindingFlags))
            {
                result[field.Name] = field.GetValue(paramsObject);
            }

            return result;
        }
    }
}
