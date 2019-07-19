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
    using MessagePack.Formatters;
    using MessagePack.Resolvers;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
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
    public class MessagePackFormatter : IJsonRpcMessageFormatter, IJsonRpcInstanceContainer
    {
        /// <summary>
        /// Special method name for progress notification.
        /// </summary>
        private const string ProgressRequestSpecialMethod = "$/progress";

        /// <summary>
        /// A value indicating whether to use LZ4 compression.
        /// </summary>
        private readonly bool compress;

        /// <summary>
        /// Object used to lock the acces to <see cref="requestProgressMap"/> and <see cref="progressMap"/>.
        /// </summary>
        private readonly object progressLock = new object();

        /// <summary>
        /// Dictionary used to map the request id to their progress id token so that the progress objects are cleaned after getting the final response.
        /// </summary>
        private readonly Dictionary<long, long> requestProgressMap = new Dictionary<long, long>();

        /// <summary>
        /// Dictionary used to map progress id token to its corresponding ProgressParamInformation instance containing the progress object and the necessary fields to report the results.
        /// </summary>
        private readonly Dictionary<long, ProgressParamInformation> progressMap = new Dictionary<long, ProgressParamInformation>();

        /// <summary>
        /// Incrementable number to assing as token for the progress objects.
        /// </summary>
        private long nextProgressId;

        /// <summary>
        /// Stores the id of the request currently being serialized so the converter can use it to create the request-progress map.
        /// </summary>
        private long? requestIdBeingSerialized;

        /// <summary>
        /// Backing field for the <see cref="Rpc"/> property.
        /// </summary>
        private JsonRpc rpc;

        /// <inheritdoc/>
        public JsonRpc Rpc
        {
            private get => this.rpc;
            set
            {
                Verify.Operation(this.rpc == null, Resources.FormatterAlreadyInUseError);

                this.rpc = value;
            }
        }

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

            return this.compress ?
                (JsonRpcMessage)MessagePackSerializer.Typeless.Deserialize(contentBuffer.AsStream(), MessagePackSerializerOptions.LZ4Default) :
                (JsonRpcMessage)MessagePackSerializer.Typeless.Deserialize(contentBuffer.AsStream());
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
                MessagePackSerializer.Typeless.Serialize(contentBuffer.AsStream(), message, MessagePackSerializerOptions.LZ4Default);
            }
            else
            {
                MessagePackSerializer.Typeless.Serialize(contentBuffer.AsStream(), message);
            }
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => MessagePackSerializer.SerializeToJson(message);

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

        private class StandardPlusIProgressOfTResolver : IFormatterResolver
        {
            private readonly MessagePackFormatter formatter;

            public StandardPlusIProgressOfTResolver(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public IMessagePackFormatter<T> GetFormatter<T>()
            {
                return new FormatterCache<T>(this.formatter).Formatter;
            }

            private class FormatterCache<T>
            {
                public readonly IMessagePackFormatter<T> Formatter;

                public FormatterCache(MessagePackFormatter formatter)
                {
                    if (MessageFormatterHelper.FindIProgressOfT(typeof(T)) != null)
                    {
                        // Call Get Formatter from IProgress Formatter?
                        this.Formatter = new IProgressOfTFormatter<T>(formatter);
                    }
                    else
                    {
                        this.Formatter = StandardResolver.Instance.GetFormatter<T>();
                    }
                }
            }
        }

        private class IProgressOfTFormatter<TIProgressOfT> : IMessagePackFormatter<TIProgressOfT>
        {
            private readonly MessagePackFormatter formatter;

            public IProgressOfTFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public void Serialize(ref MessagePackWriter writer, TIProgressOfT value, MessagePackSerializerOptions options)
            {
                // if the requestId is empty it means the Progress object comes from a response or a notification
                if (this.formatter.requestIdBeingSerialized == null)
                {
                    throw new NotSupportedException("IProgress<T> objects should not be part of any response or notification.");
                }

                lock (this.formatter.progressLock)
                {
                    long progressId = this.formatter.nextProgressId++;
                    this.formatter.requestProgressMap.Add(this.formatter.requestIdBeingSerialized.Value, progressId);

                    this.formatter.progressMap.Add(progressId, new ProgressParamInformation(value));

                    writer.Write(progressId);
                }
            }

            public TIProgressOfT Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                long token = reader.ReadInt64();

                if (this.formatter.progressMap.TryGetValue(token, out ProgressParamInformation progressInfo))
                {
                    Type progressType = typeof(JsonProgress<>).MakeGenericType(progressInfo.ValueType);
                    return Activator.CreateInstance(progressType, new object[] { this.formatter.Rpc, token });
                }
            }
        }

        private class JsonProgress<T> : IProgress<T>
        {
            private readonly JsonRpc rpc;
            private readonly long? token;

            public JsonProgress(JsonRpc rpc, long? token)
            {
                this.rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
                this.token = token ?? throw new ArgumentNullException(nameof(token));
            }

            public void Report(T value)
            {
                this.rpc.NotifyAsync(ProgressRequestSpecialMethod, this.token, value).Forget();
            }
        }
    }
}
