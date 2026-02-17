// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// <para>
/// This formatter uses the <see href="https://github.com/AArnott/Nerdbank.MessagePack">Nerdbank.MessagePack</see> serializer.
/// </para>
/// <para>
/// This formatter prioritizes being trim and NativeAOT safe. As such, it uses <see cref="JsonRpc.LoadTypeTrimSafe(string, string?)"/> instead of <see cref="JsonRpc.LoadType(string, string?)"/> to load exception types to be deserialized.
/// This trim-friendly method should be overridden to return types that are particularly interesting to the application.
/// </para>
/// </remarks>
public partial class NerdbankMessagePackFormatter : FormatterBase, IJsonRpcMessageFormatter, IJsonRpcFormatterTracingCallbacks, IJsonRpcMessageFactory
{
    /// <summary>
    /// The default serializer to use for user data, and a good basis for any custom values for
    /// <see cref="UserDataSerializer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This serializer is configured with <see cref="MessagePackSerializer.InternStrings"/> set to <see langword="true" />
    /// and various <see cref="MessagePackSerializer.Converters"/> and <see cref="MessagePackSerializer.ConverterFactories"/>.
    /// </para>
    /// <para>
    /// When deviating from this default, doing so while preserving the converters and converter factories from the default
    /// is highly recommended.
    /// It should be done once and stored in a <see langword="static" /> field and reused for the lifetime of the application
    /// to avoid repeated startup costs associated with building up the converter tree.
    /// </para>
    /// </remarks>
    public static readonly MessagePackSerializer DefaultSerializer = new MessagePackSerializer()
    {
        InternStrings = true,
        ConverterFactories = [ConverterFactory.Instance],
        Converters =
            [
                GetRpcMarshalableConverter(Witness.GeneratedTypeShapeProvider.GetTypeShapeOrThrow<IDisposable>()),
                PipeConverters.PipeReaderConverter.DefaultInstance,
                PipeConverters.PipeWriterConverter.DefaultInstance,
                PipeConverters.DuplexPipeConverter.DefaultInstance,
                PipeConverters.StreamConverter.DefaultInstance,

                // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
                RequestIdConverter.Instance,

                ExceptionConverter<Exception>.Instance,
            ],
        DerivedTypeUnions = [
            DerivedTypeUnion.CreateDisabled(typeof(JsonRpcMessage)),
        ],
    }.WithObjectConverter();

    private static readonly ProxyFactory ProxyFactory = ProxyFactory.NoDynamic;

    private static readonly JsonRpcProxyOptions DefaultRpcMarshalableProxyOptions = new JsonRpcProxyOptions(JsonRpcProxyOptions.Default) { AcceptProxyWithExtraInterfaces = true, IsFrozen = true };

    /// <summary>
    /// The serializer context to use for top-level RPC messages.
    /// </summary>
    private readonly MessagePackSerializer envelopeSerializer;

    private readonly ToStringHelper serializationToStringHelper = new();

    private readonly ToStringHelper deserializationToStringHelper = new();

    /// <summary>
    /// Tracks recursion count while serializing or deserializing an exception.
    /// </summary>
    /// <remarks>
    /// This is placed here (<em>outside</em> the generic <see cref="ExceptionConverter{T}"/> class)
    /// so that it's one counter shared across all exception types that may be serialized or deserialized.
    /// </remarks>
    private readonly ThreadLocal<int> exceptionRecursionCounter = new();

    /// <summary>
    /// The serializer to use for user data (e.g. arguments, return values and errors).
    /// </summary>
    private MessagePackSerializer userDataSerializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="NerdbankMessagePackFormatter"/> class.
    /// </summary>
    public NerdbankMessagePackFormatter()
    {
        // Set up initial options for our own message types.
        this.envelopeSerializer = DefaultSerializer with
        {
            StartingContext = new SerializationContext()
            {
                [SerializationContextExtensions.FormatterKey] = this,
            },
        };

        // Create a serializer for user data.
        // At the moment, we just reuse the same serializer for envelope and user data.
        this.userDataSerializer = this.envelopeSerializer;
    }

    /// <summary>
    /// Gets the shape provider for user data types.
    /// </summary>
    public required ITypeShapeProvider TypeShapeProvider { get; init; }

    /// <summary>
    /// Gets the configured serializer to use for request arguments, result values and error data.
    /// </summary>
    /// <remarks>
    /// When setting this property, basing the new value on <see cref="DefaultSerializer"/> is highly recommended.
    /// </remarks>
    public MessagePackSerializer UserDataSerializer
    {
        get => this.userDataSerializer;
        [MemberNotNull(nameof(userDataSerializer))]
        init
        {
            Requires.NotNull(value);

            // Customizing the input serializer to set the FormatterKey is necessary for our stateful converters.
            // Doing this does NOT destroy the converter graph that may be cached because mutating the StartingContext
            // property does not invalidate the graph.
            this.userDataSerializer = value with
            {
                StartingContext = new SerializationContext()
                {
                    [SerializationContextExtensions.FormatterKey] = this,
                },
            };
        }
    }

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
    {
        JsonRpcMessage message = this.envelopeSerializer.Deserialize(contentBuffer, PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc.Default.JsonRpcMessage)
            ?? throw new MessagePackSerializationException("Failed to deserialize JSON-RPC message.");

        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        this.deserializationToStringHelper.Activate((RawMessagePack)contentBuffer, this.UserDataSerializer);
        try
        {
            tracingCallbacks?.OnMessageDeserialized(message, this.deserializationToStringHelper);
        }
        finally
        {
            this.deserializationToStringHelper.Deactivate();
        }

        return message;
    }

    /// <inheritdoc/>
    public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
    {
        var writer = new MessagePackWriter(bufferWriter);
        try
        {
            this.envelopeSerializer.Serialize(ref writer, message, PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc.Default.JsonRpcMessage);
            writer.Flush();
        }
        catch (Exception ex)
        {
            throw new MessagePackSerializationException(string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcMessage, ex.GetType().Name, ex.Message), ex);
        }
    }

    /// <inheritdoc/>
    public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack
        ? this.UserDataSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack)
        : throw new NotSupportedException();

    /// <inheritdoc/>
    Protocol.JsonRpcRequest IJsonRpcMessageFactory.CreateRequestMessage() => new OutboundJsonRpcRequest(this);

    /// <inheritdoc/>
    Protocol.JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() => new JsonRpcError(this);

    /// <inheritdoc/>
    Protocol.JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() => new JsonRpcResult(this);

    void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage)
    {
        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        this.serializationToStringHelper.Activate((RawMessagePack)encodedMessage, this.UserDataSerializer);
        try
        {
            tracingCallbacks?.OnMessageSerialized(message, this.serializationToStringHelper);
        }
        finally
        {
            this.serializationToStringHelper.Deactivate();
        }
    }

    private protected override MessageFormatterRpcMarshaledContextTracker CreateMessageFormatterRpcMarshaledContextTracker(JsonRpc rpc) => new MessageFormatterRpcMarshaledContextTracker.PolyTypeShape(rpc, ProxyFactory, this, this.TypeShapeProvider);

    private static MessagePackConverter<T> GetRpcMarshalableConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.PublicProperties)] T>(ITypeShape<T> shape)
        where T : class
    {
        if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(
            typeof(T),
            DefaultRpcMarshalableProxyOptions,
            out JsonRpcProxyOptions? proxyOptions,
            out JsonRpcTargetOptions? targetOptions,
            out RpcMarshalableAttribute? attribute))
        {
            return new RpcMarshalableConverter<T>(shape, proxyOptions, targetOptions, attribute);
        }

        throw new NotSupportedException($"Type '{typeof(T).FullName}' is not supported for RPC Marshaling.");
    }

    /// <summary>
    /// Reads a string with an optimized path for the value "2.0".
    /// </summary>
    /// <param name="reader">The reader to use.</param>
    /// <returns>The decoded string.</returns>
    private static string ReadProtocolVersion(ref MessagePackReader reader)
    {
        // Recognize "2.0" since we expect it and can avoid decoding and allocating a new string for it.
        return Version2.TryRead(ref reader)
            ? Version2.Value
            : reader.ReadString() ?? throw new MessagePackSerializationException(Resources.RequiredArgumentMissing);
    }

    /// <summary>
    /// Writes the JSON-RPC version property name and value in a highly optimized way.
    /// </summary>
    private static void WriteProtocolVersionPropertyAndValue(ref MessagePackWriter writer, string version)
    {
        writer.Write(VersionPropertyName);
        writer.Write(version);
    }

    private static void ReadUnknownProperty(ref MessagePackReader reader, in SerializationContext context, ref Dictionary<string, RawMessagePack>? topLevelProperties)
    {
        topLevelProperties ??= new Dictionary<string, RawMessagePack>(StringComparer.Ordinal);
        string name = context.GetConverter<string>(Witness.GeneratedTypeShapeProvider).Read(ref reader, context) ?? throw new MessagePackSerializationException("Unexpected nil at property name position.");
        topLevelProperties.Add(name, reader.ReadRaw(context));
    }

    private static T ActivateAssociatedType<T>(ITypeShape shape, Type associatedType)
        where T : class
        => (T?)((IObjectTypeShape?)shape.GetAssociatedTypeShape(associatedType))?.GetDefaultConstructor()?.Invoke() ?? throw new InvalidOperationException($"Missing associated type from {shape.Type.FullName} to {associatedType.FullName}.");

    private ITypeShape GetUserDataShape(Type type)
    {
        type = NormalizeType(type);

        // We prefer to get the shape from the user shape provider, but will fallback to our own for built-in types.
        // But if that fails too, try again with Resolve on the user shape provider so that it throws an exception explaining that the user needs to provide it.
        return this.TypeShapeProvider.GetTypeShape(type) ?? Witness.GeneratedTypeShapeProvider.GetTypeShape(type) ?? this.TypeShapeProvider.GetTypeShapeOrThrow(type);
    }

    private void WriteUserData(ref MessagePackWriter writer, object? value, Type? valueType, SerializationContext context)
    {
        if (value is null)
        {
            writer.WriteNil();
        }
        else
        {
            if (valueType == typeof(void) || valueType == typeof(object))
            {
                valueType = null;
            }

            ITypeShape valueShape = this.GetUserDataShape(valueType ?? value.GetType());
            this.UserDataSerializer.SerializeObject(ref writer, value, valueShape, context.CancellationToken);
        }
    }

    /// <summary>
    /// Converts JSON-RPC messages to and from MessagePack format.
    /// </summary>
    internal class JsonRpcMessageConverter : MessagePackConverter<JsonRpcMessage>
    {
        /// <summary>
        /// Reads a JSON-RPC message from the specified MessagePack reader.
        /// </summary>
        /// <param name="reader">The MessagePack reader to read from.</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>The deserialized JSON-RPC message.</returns>
        public override JsonRpcMessage? Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            MessagePackReader readAhead = reader.CreatePeekReader();
            int propertyCount = readAhead.ReadMapHeader();

            for (int i = 0; i < propertyCount; i++)
            {
                if (MethodPropertyName.TryRead(ref readAhead))
                {
                    return context.GetConverter<Protocol.JsonRpcRequest>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ResultPropertyName.TryRead(ref readAhead))
                {
                    return context.GetConverter<Protocol.JsonRpcResult>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ErrorPropertyName.TryRead(ref readAhead))
                {
                    return context.GetConverter<Protocol.JsonRpcError>(context.TypeShapeProvider).Read(ref reader, context);
                }

                // This property doesn't tell us the message type.
                // Skip its name and value.
                readAhead.Skip(context);
                readAhead.Skip(context);
            }

            throw new UnrecognizedJsonRpcMessageException();
        }

        /// <summary>
        /// Writes a JSON-RPC message to the specified MessagePack writer.
        /// </summary>
        /// <param name="writer">The MessagePack writer to write to.</param>
        /// <param name="value">The JSON-RPC message to write.</param>
        /// <param name="context">The serialization context.</param>
        public override void Write(ref MessagePackWriter writer, in JsonRpcMessage? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            context.DepthStep();

            using (formatter.TrackSerialization(value))
            {
                switch (value)
                {
                    case Protocol.JsonRpcRequest request:
                        context.GetConverter<Protocol.JsonRpcRequest>(context.TypeShapeProvider).Write(ref writer, request, context);
                        break;
                    case Protocol.JsonRpcResult result:
                        context.GetConverter<Protocol.JsonRpcResult>(context.TypeShapeProvider).Write(ref writer, result, context);
                        break;
                    case Protocol.JsonRpcError error:
                        context.GetConverter<Protocol.JsonRpcError>(context.TypeShapeProvider).Write(ref writer, error, context);
                        break;
                    default:
                        throw new NotSupportedException("Unexpected JsonRpcMessage-derived type: " + value.GetType().Name);
                }
            }
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }

    /// <summary>
    /// Converts a JSON-RPC request message to and from MessagePack format.
    /// </summary>
    internal class JsonRpcRequestConverter : MessagePackConverter<Protocol.JsonRpcRequest>
    {
        /// <summary>
        /// Reads a JSON-RPC request message from the specified MessagePack reader.
        /// </summary>
        /// <param name="reader">The MessagePack reader to read from.</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>The deserialized JSON-RPC request message.</returns>
        public override Protocol.JsonRpcRequest? Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            var result = new JsonRpcRequest(formatter)
            {
                OriginalMessagePack = (RawMessagePack)reader.Sequence,
            };

            Dictionary<string, RawMessagePack>? topLevelProperties = null;

            int propertyCount = reader.ReadMapHeader();
            for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                if (VersionPropertyName.TryRead(ref reader))
                {
                    result.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(ref reader))
                {
                    result.RequestId = context.GetConverter<RequestId>(null).Read(ref reader, context);
                }
                else if (MethodPropertyName.TryRead(ref reader))
                {
                    result.Method = context.GetConverter<string>(Witness.GeneratedTypeShapeProvider).Read(ref reader, context);
                }
                else if (ParamsPropertyName.TryRead(ref reader))
                {
                    SequencePosition paramsTokenStartPosition = reader.Position;

                    // Parse out the arguments into a dictionary or array, but don't deserialize them because we don't yet know what types to deserialize them to.
                    switch (reader.NextMessagePackType)
                    {
                        case MessagePackType.Array:
                            var positionalArgs = new RawMessagePack[reader.ReadArrayHeader()];
                            for (int i = 0; i < positionalArgs.Length; i++)
                            {
                                positionalArgs[i] = reader.ReadRaw(context);
                            }

                            result.MsgPackPositionalArguments = positionalArgs;
                            break;
                        case MessagePackType.Map:
                            int namedArgsCount = reader.ReadMapHeader();
                            var namedArgs = new Dictionary<string, RawMessagePack>(namedArgsCount, StringComparer.Ordinal);
                            for (int i = 0; i < namedArgsCount; i++)
                            {
                                // Use a string converter so that strings can be interned.
                                string propertyName = context.GetConverter<string>(Witness.GeneratedTypeShapeProvider).Read(ref reader, context) ?? throw new MessagePackSerializationException(Resources.UnexpectedNullValueInMap);
                                namedArgs.Add(propertyName, reader.ReadRaw(context));
                            }

                            result.MsgPackNamedArguments = namedArgs;
                            break;
                        case MessagePackType.Nil:
                            result.MsgPackPositionalArguments = [];
                            reader.ReadNil();
                            break;
                        case MessagePackType type:
                            throw new MessagePackSerializationException("Expected a map or array of arguments but got " + type);
                    }

                    result.MsgPackArguments = (RawMessagePack)reader.Sequence.Slice(paramsTokenStartPosition, reader.Position);
                }
                else if (TraceParentPropertyName.TryRead(ref reader))
                {
                    TraceParent traceParent = context.GetConverter<TraceParent>(null).Read(ref reader, context);
                    result.TraceParent = traceParent.ToString();
                }
                else if (TraceStatePropertyName.TryRead(ref reader))
                {
                    result.TraceState = ReadTraceState(ref reader, context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties);
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(formatter, topLevelProperties);
            }

            formatter.TryHandleSpecialIncomingMessage(result);

            return result;
        }

        /// <summary>
        /// Writes a JSON-RPC request message to the specified MessagePack writer.
        /// </summary>
        /// <param name="writer">The MessagePack writer to write to.</param>
        /// <param name="value">The JSON-RPC request message to write.</param>
        /// <param name="context">The serialization context.</param>
        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcRequest? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            var topLevelPropertyBag = (TopLevelPropertyBag?)(value as IMessageWithTopLevelPropertyBag)?.TopLevelPropertyBag;

            int mapElementCount = value.RequestId.IsEmpty ? 3 : 4;
            if (value.TraceParent?.Length > 0)
            {
                mapElementCount++;
                if (value.TraceState?.Length > 0)
                {
                    mapElementCount++;
                }
            }

            mapElementCount += topLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            if (!value.RequestId.IsEmpty)
            {
                writer.Write(IdPropertyName);
                context.GetConverter<RequestId>(Witness.GeneratedTypeShapeProvider).Write(ref writer, value.RequestId, context);
            }

            writer.Write(MethodPropertyName);
            writer.Write(value.Method);

            writer.Write(ParamsPropertyName);
            if (value.ArgumentsList is not null)
            {
                writer.WriteArrayHeader(value.ArgumentsList.Count);

                for (int i = 0; i < value.ArgumentsList.Count; i++)
                {
                    formatter.WriteUserData(ref writer, value.ArgumentsList[i], value.ArgumentListDeclaredTypes?[i], context);
                }
            }
            else if (value.NamedArguments is not null)
            {
                writer.WriteMapHeader(value.NamedArguments.Count);
                foreach (KeyValuePair<string, object?> entry in value.NamedArguments)
                {
                    writer.Write(entry.Key);
                    formatter.WriteUserData(ref writer, entry.Value, value.NamedArgumentDeclaredTypes?[entry.Key], context);
                }
            }
            else if (value.Arguments is not null)
            {
                // We don't understand the format the Arguments object is in, so we just write it as-is.
                // This requires that a type shape be known for it in the user's supplied type shape provider.
                // We expect this to serialize as a map.
                MessagePackConverter namedArgsConverter;
                try
                {
                    namedArgsConverter = context.GetConverter(value.Arguments.GetType(), formatter.TypeShapeProvider);
                }
                catch (NotSupportedException ex)
                {
                    throw new MessagePackSerializationException("No type shape available for custom named arguments type. Apply [GenerateShape] to the type or wrap the argument object with NamedArgs.Create.", ex);
                }

                namedArgsConverter.WriteObject(ref writer, value.Arguments, context);
            }
            else
            {
                writer.WriteNil();
            }

            if (value.TraceParent?.Length > 0)
            {
                writer.Write(TraceParentPropertyName);
                context.GetConverter<TraceParent>(Witness.GeneratedTypeShapeProvider).Write(ref writer, new TraceParent(value.TraceParent), context);

                if (value.TraceState?.Length > 0)
                {
                    writer.Write(TraceStatePropertyName);
                    WriteTraceState(ref writer, value.TraceState);
                }
            }

            topLevelPropertyBag?.WriteProperties(ref writer);
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;

        private static void WriteTraceState(ref MessagePackWriter writer, string traceState)
        {
            ReadOnlySpan<char> traceStateChars = traceState.AsSpan();

            // Count elements first so we can write the header.
            int elementCount = 1;
            int commaIndex;
            while ((commaIndex = traceStateChars.IndexOf(',')) >= 0)
            {
                elementCount++;
                traceStateChars = traceStateChars.Slice(commaIndex + 1);
            }

            // For every element, we have a key and value to record.
            writer.WriteArrayHeader(elementCount * 2);

            traceStateChars = traceState.AsSpan();
            while ((commaIndex = traceStateChars.IndexOf(',')) >= 0)
            {
                ReadOnlySpan<char> element = traceStateChars.Slice(0, commaIndex);
                WritePair(ref writer, element);
                traceStateChars = traceStateChars.Slice(commaIndex + 1);
            }

            // Write out the last one.
            WritePair(ref writer, traceStateChars);

            static void WritePair(ref MessagePackWriter writer, ReadOnlySpan<char> pair)
            {
                int equalsIndex = pair.IndexOf('=');
                ReadOnlySpan<char> key = pair.Slice(0, equalsIndex);
                ReadOnlySpan<char> value = pair.Slice(equalsIndex + 1);
                writer.Write(key);
                writer.Write(value);
            }
        }

        private static unsafe string ReadTraceState(ref MessagePackReader reader, SerializationContext context)
        {
            int elements = reader.ReadArrayHeader();
            if (elements % 2 != 0)
            {
                throw new NotSupportedException("Odd number of elements not expected.");
            }

            // With care, we could probably assemble this string with just two allocations (the string + a char[]).
            var resultBuilder = new StringBuilder();
            for (int i = 0; i < elements; i += 2)
            {
                if (resultBuilder.Length > 0)
                {
                    resultBuilder.Append(',');
                }

                // We assume the key is a frequent string, and the value is unique,
                // so we optimize whether to use string interning or not on that basis.
                resultBuilder.Append(context.GetConverter<string>(Witness.GeneratedTypeShapeProvider).Read(ref reader, context));
                resultBuilder.Append('=');
                resultBuilder.Append(reader.ReadString());
            }

            return resultBuilder.ToString();
        }
    }

    /// <summary>
    /// Converts a JSON-RPC result message to and from MessagePack format.
    /// </summary>
    internal class JsonRpcResultConverter : MessagePackConverter<Protocol.JsonRpcResult>
    {
        /// <summary>
        /// Reads a JSON-RPC result message from the specified MessagePack reader.
        /// </summary>
        /// <param name="reader">The MessagePack reader to read from.</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>The deserialized JSON-RPC result message.</returns>
        public override Protocol.JsonRpcResult Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            context.DepthStep();

            var result = new JsonRpcResult(formatter)
            {
                OriginalMessagePack = (RawMessagePack)reader.Sequence,
            };

            Dictionary<string, RawMessagePack>? topLevelProperties = null;

            int propertyCount = reader.ReadMapHeader();
            for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                if (VersionPropertyName.TryRead(ref reader))
                {
                    result.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(ref reader))
                {
                    result.RequestId = context.GetConverter<RequestId>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ResultPropertyName.TryRead(ref reader))
                {
                    result.MsgPackResult = reader.ReadRaw(context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties);
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(formatter, topLevelProperties);
            }

            return result;
        }

        /// <summary>
        /// Writes a JSON-RPC result message to the specified MessagePack writer.
        /// </summary>
        /// <param name="writer">The MessagePack writer to write to.</param>
        /// <param name="value">The JSON-RPC result message to write.</param>
        /// <param name="context">The serialization context.</param>
        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcResult? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            var topLevelPropertyBagMessage = value as IMessageWithTopLevelPropertyBag;

            int mapElementCount = 3;
            mapElementCount += (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            writer.Write(IdPropertyName);
            context.GetConverter<RequestId>(context.TypeShapeProvider).Write(ref writer, value.RequestId, context);

            writer.Write(ResultPropertyName);

            if (value.Result is null)
            {
                writer.WriteNil();
            }
            else
            {
                formatter.WriteUserData(ref writer, value.Result, value.ResultDeclaredType, context);
            }

            (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.WriteProperties(ref writer);
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }

    /// <summary>
    /// Converts a JSON-RPC error message to and from MessagePack format.
    /// </summary>
    internal class JsonRpcErrorConverter : MessagePackConverter<Protocol.JsonRpcError>
    {
        /// <summary>
        /// Reads a JSON-RPC error message from the specified MessagePack reader.
        /// </summary>
        /// <param name="reader">The MessagePack reader to read from.</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>The deserialized JSON-RPC error message.</returns>
        public override Protocol.JsonRpcError Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            var error = new JsonRpcError(formatter)
            {
                OriginalMessagePack = (RawMessagePack)reader.Sequence,
            };

            Dictionary<string, RawMessagePack>? topLevelProperties = null;

            context.DepthStep();
            int propertyCount = reader.ReadMapHeader();
            for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
            {
                if (VersionPropertyName.TryRead(ref reader))
                {
                    error.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(ref reader))
                {
                    error.RequestId = context.GetConverter<RequestId>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ErrorPropertyName.TryRead(ref reader))
                {
                    error.Error = context.GetConverter<Protocol.JsonRpcError.ErrorDetail>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties);
                }
            }

            if (topLevelProperties is not null)
            {
                error.TopLevelPropertyBag = new TopLevelPropertyBag(formatter, topLevelProperties);
            }

            return error;
        }

        /// <summary>
        /// Writes a JSON-RPC error message to the specified MessagePack writer.
        /// </summary>
        /// <param name="writer">The MessagePack writer to write to.</param>
        /// <param name="value">The JSON-RPC error message to write.</param>
        /// <param name="context">The serialization context.</param>
        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcError? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            var topLevelPropertyBag = (TopLevelPropertyBag?)(value as IMessageWithTopLevelPropertyBag)?.TopLevelPropertyBag;

            context.DepthStep();
            int mapElementCount = 3;
            mapElementCount += topLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            writer.Write(IdPropertyName);
            context.GetConverter<RequestId>(context.TypeShapeProvider)
                .Write(ref writer, value.RequestId, context);

            writer.Write(ErrorPropertyName);
            context.GetConverter<Protocol.JsonRpcError.ErrorDetail>(context.TypeShapeProvider)
                .Write(ref writer, value.Error, context);

            topLevelPropertyBag?.WriteProperties(ref writer);
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }

    /// <summary>
    /// Converts a JSON-RPC error detail to and from MessagePack format.
    /// </summary>
    internal class JsonRpcErrorDetailConverter : MessagePackConverter<Protocol.JsonRpcError.ErrorDetail>
    {
        private static readonly MessagePackString CodePropertyName = new("code");
        private static readonly MessagePackString MessagePropertyName = new("message");
        private static readonly MessagePackString DataPropertyName = new("data");

        /// <summary>
        /// Reads a JSON-RPC error detail from the specified MessagePack reader.
        /// </summary>
        /// <param name="reader">The MessagePack reader to read from.</param>
        /// <param name="context">The serialization context.</param>
        /// <returns>The deserialized JSON-RPC error detail.</returns>
        public override Protocol.JsonRpcError.ErrorDetail Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            context.DepthStep();

            var result = new JsonRpcError.ErrorDetail(formatter);

            int propertyCount = reader.ReadMapHeader();
            for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
            {
                if (CodePropertyName.TryRead(ref reader))
                {
                    result.Code = context.GetConverter<JsonRpcErrorCode>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (MessagePropertyName.TryRead(ref reader))
                {
                    result.Message = context.GetConverter<string>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (DataPropertyName.TryRead(ref reader))
                {
                    result.MsgPackData = reader.ReadRaw(context);
                }
                else
                {
                    reader.Skip(context); // skip the key
                    reader.Skip(context); // skip the value
                }
            }

            return result;
        }

        /// <summary>
        /// Writes a JSON-RPC error detail to the specified MessagePack writer.
        /// </summary>
        /// <param name="writer">The MessagePack writer to write to.</param>
        /// <param name="value">The JSON-RPC error detail to write.</param>
        /// <param name="context">The serialization context.</param>
        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to user data context")]
        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcError.ErrorDetail? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            context.DepthStep();

            writer.WriteMapHeader(3);

            writer.Write(CodePropertyName);
            context.GetConverter<JsonRpcErrorCode>(context.TypeShapeProvider)
                .Write(ref writer, value.Code, context);

            writer.Write(MessagePropertyName);
            writer.Write(value.Message);

            writer.Write(DataPropertyName);
            if (value.Data is null)
            {
                writer.WriteNil();
            }
            else
            {
                // We generally leave error data for the user to provide the shape for.
                // But for CommonErrorData, we can take responsibility for that.
                // We also take responsibility for Exception serialization (for now).
                ITypeShapeProvider provider = value.Data is CommonErrorData or Exception ? Witness.GeneratedTypeShapeProvider : formatter.TypeShapeProvider;
                Type declaredType = value.Data is Exception ? typeof(Exception) : value.Data.GetType();
                context.GetConverter(declaredType, provider).WriteObject(ref writer, value.Data, context);
            }
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }

    private class TopLevelPropertyBag : TopLevelPropertyBagBase
    {
        private readonly NerdbankMessagePackFormatter formatter;
        private readonly IReadOnlyDictionary<string, RawMessagePack>? inboundUnknownProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an incoming message.
        /// </summary>
        /// <param name="formatter">The owning formatter.</param>
        /// <param name="inboundUnknownProperties">The map of unrecognized inbound properties.</param>
        internal TopLevelPropertyBag(NerdbankMessagePackFormatter formatter, IReadOnlyDictionary<string, RawMessagePack> inboundUnknownProperties)
            : base(isOutbound: false)
        {
            this.formatter = formatter;
            this.inboundUnknownProperties = inboundUnknownProperties;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an outbound message.
        /// </summary>
        /// <param name="formatter">The owning formatter.</param>
        internal TopLevelPropertyBag(NerdbankMessagePackFormatter formatter)
            : base(isOutbound: true)
        {
            this.formatter = formatter;
        }

        internal int PropertyCount => this.inboundUnknownProperties?.Count ?? this.OutboundProperties?.Count ?? 0;

        /// <summary>
        /// Writes the properties tracked by this collection to a messagepack writer.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        internal void WriteProperties(ref MessagePackWriter writer)
        {
            if (this.inboundUnknownProperties is not null)
            {
                // We're actually re-transmitting an incoming message (remote target feature).
                // We need to copy all the properties that were in the original message.
                // Don't implement this without enabling the tests for the scenario found in JsonRpcRemoteTargetMessagePackFormatterTests.cs.
                // The tests fail for reasons even without this support, so there's work to do beyond just implementing this.
                throw new NotImplementedException();

                ////foreach (KeyValuePair<string, ReadOnlySequence<byte>> entry in this.inboundUnknownProperties)
                ////{
                ////    writer.Write(entry.Key);
                ////    writer.Write(entry.Value);
                ////}
            }
            else
            {
                foreach (KeyValuePair<string, (Type DeclaredType, object? Value)> entry in this.OutboundProperties)
                {
                    writer.Write(entry.Key);
                    this.formatter.userDataSerializer.SerializeObject(ref writer, entry.Value.Value, this.formatter.TypeShapeProvider.GetTypeShapeOrThrow(entry.Value.DeclaredType));
                }
            }
        }

        protected internal override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            if (this.inboundUnknownProperties is null)
            {
                throw new InvalidOperationException(Resources.InboundMessageOnly);
            }

            value = default;

            if (this.inboundUnknownProperties.TryGetValue(name, out RawMessagePack serializedValue) is true)
            {
                value = this.formatter.userDataSerializer.Deserialize(serializedValue, this.formatter.TypeShapeProvider.GetTypeShapeOrThrow<T>());
                return true;
            }

            return false;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class OutboundJsonRpcRequest(NerdbankMessagePackFormatter formatter) : JsonRpcRequestBase
    {
        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(formatter);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class JsonRpcRequest : JsonRpcRequestBase, IJsonRpcMessagePackRetention
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcRequest(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override int ArgumentCount => this.MsgPackNamedArguments?.Count ?? this.MsgPackPositionalArguments?.Count ?? base.ArgumentCount;

        public override IEnumerable<string>? ArgumentNames => this.MsgPackNamedArguments?.Keys;

        public RawMessagePack OriginalMessagePack { get; internal set; }

        internal RawMessagePack MsgPackArguments { get; set; }

        internal IReadOnlyDictionary<string, RawMessagePack>? MsgPackNamedArguments { get; set; }

        internal IReadOnlyList<RawMessagePack>? MsgPackPositionalArguments { get; set; }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
        {
            using (this.formatter.TrackDeserialization(this, parameters))
            {
                if (parameters.Length == 1 && this.MsgPackNamedArguments is not null)
                {
                    if (this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.UseSingleObjectParameterDeserialization ?? false)
                    {
                        var reader = new MessagePackReader(this.MsgPackArguments);
                        try
                        {
                            typedArguments[0] = this.formatter.userDataSerializer.DeserializeObject(
                                ref reader,
                                this.formatter.TypeShapeProvider.GetTypeShapeOrThrow(parameters[0].ParameterType));

                            return ArgumentMatchResult.Success;
                        }
                        catch (MessagePackSerializationException)
                        {
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }
        }

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            // If anyone asks us for an argument *after* we've been told deserialization is done, there's something very wrong.
            Assumes.True(this.MsgPackNamedArguments is not null || this.MsgPackPositionalArguments is not null);

            RawMessagePack msgpackArgument = default;
            if (position >= 0 && this.MsgPackPositionalArguments?.Count > position)
            {
                msgpackArgument = this.MsgPackPositionalArguments[position];
            }
            else if (name is not null && this.MsgPackNamedArguments is not null)
            {
                this.MsgPackNamedArguments.TryGetValue(name, out msgpackArgument);
            }

            if (msgpackArgument.MsgPack.IsEmpty)
            {
                value = null;
                return false;
            }

            using (this.formatter.TrackDeserialization(this))
            {
                try
                {
                    MessagePackReader reader = new(msgpackArgument);
                    value = this.formatter.userDataSerializer.DeserializeObject(
                        ref reader,
                        this.formatter.GetUserDataShape(typeHint ?? typeof(object)));

                    return true;
                }
                catch (MessagePackSerializationException ex)
                {
                    if (this.formatter.JsonRpc?.TraceSource.Switch.ShouldTrace(TraceEventType.Warning) ?? false)
                    {
                        this.formatter.JsonRpc.TraceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.MethodArgumentDeserializationFailure, Resources.FailureDeserializingRpcArgument, name, position, typeHint, ex);
                    }

                    throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
                }
            }
        }

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            this.MsgPackNamedArguments = null;
            this.MsgPackPositionalArguments = null;
            this.TopLevelPropertyBag = null;
            this.MsgPackArguments = default;
            this.OriginalMessagePack = default;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private partial class JsonRpcResult(NerdbankMessagePackFormatter formatter) : JsonRpcResultBase, IJsonRpcMessagePackRetention
    {
        private Exception? resultDeserializationException;

        public RawMessagePack OriginalMessagePack { get; internal set; }

        internal RawMessagePack MsgPackResult { get; set; }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            return this.MsgPackResult.MsgPack.IsEmpty
                ? (T)this.Result!
                : formatter.userDataSerializer.Deserialize(this.MsgPackResult, formatter.TypeShapeProvider.GetTypeShapeOrThrow<T>())
                ?? throw new MessagePackSerializationException("Failed to deserialize result.");
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(!this.MsgPackResult.MsgPack.IsEmpty, "Result is no longer available or has already been deserialized.");

            try
            {
                using (formatter.TrackDeserialization(this))
                {
                    // The non-generic JsonRpc.InvokeAsync method sets System.Object as the return type just as a filler,
                    // but we can't deserialize to System.Object.
                    MessagePackReader reader = new(this.MsgPackResult);
                    this.Result = formatter.userDataSerializer.DeserializeObject(ref reader, formatter.GetUserDataShape(resultType));
                }

                this.MsgPackResult = default;
            }
            catch (MessagePackSerializationException ex)
            {
                // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                this.resultDeserializationException = ex;
            }
        }

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            this.MsgPackResult = default;
            this.OriginalMessagePack = default;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(formatter);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class JsonRpcError(NerdbankMessagePackFormatter formatter) : JsonRpcErrorBase, IJsonRpcMessagePackRetention
    {
        public RawMessagePack OriginalMessagePack { get; internal set; }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(formatter);

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            if (this.Error is ErrorDetail privateDetail)
            {
                privateDetail.MsgPackData = default;
            }

            this.OriginalMessagePack = default;
        }

        internal new class ErrorDetail(NerdbankMessagePackFormatter formatter) : Protocol.JsonRpcError.ErrorDetail
        {
            internal ReadOnlySequence<byte> MsgPackData { get; set; }

            public override object? GetData(Type dataType)
            {
                Requires.NotNull(dataType, nameof(dataType));
                if (this.MsgPackData.IsEmpty)
                {
                    return this.Data;
                }

                MessagePackReader reader = new(this.MsgPackData);
                try
                {
                    return
                        (dataType == typeof(Exception) || dataType == typeof(CommonErrorData)) ? formatter.envelopeSerializer.DeserializeObject(ref reader, Witness.GeneratedTypeShapeProvider.GetTypeShapeOrThrow(dataType)) :
                        formatter.userDataSerializer.DeserializeObject(ref reader, formatter.TypeShapeProvider.GetTypeShapeOrThrow(dataType));
                }
                catch (MessagePackSerializationException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        reader = new(this.MsgPackData);
                        return formatter.envelopeSerializer.DeserializePrimitives(ref reader);
                    }
                    catch (MessagePackSerializationException)
                    {
                        return null;
                    }
                }
            }

            protected internal override void SetExpectedDataType(Type dataType)
            {
                Verify.Operation(!this.MsgPackData.IsEmpty, "Data is no longer available or has already been deserialized.");

                this.Data = this.GetData(dataType);

                // Clear the source now that we've deserialized to prevent GetData from attempting
                // deserialization later when the buffer may be recycled on another thread.
                this.MsgPackData = default;
            }
        }
    }

    private class ConverterFactory : IMessagePackConverterFactory, ITypeShapeFunc
    {
        internal static readonly ConverterFactory Instance = new();

        private ConverterFactory()
        {
        }

        public MessagePackConverter? CreateConverter(Type type, ITypeShape? shape, in ConverterContext context)
            => shape is not null && (
               MessageFormatterProgressTracker.CanDeserialize(type) || MessageFormatterProgressTracker.CanSerialize(type) ||
               TrackerHelpers.IsIAsyncEnumerable(type) ||
               TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(type) is Type ||
               MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(shape, DefaultRpcMarshalableProxyOptions, out JsonRpcProxyOptions? proxyOptions, out JsonRpcTargetOptions? targetOptions, out RpcMarshalableAttribute? attribute) ||
               typeof(Exception).IsAssignableFrom(type))
               ? this.Invoke(shape) : null;

        object? ITypeShapeFunc.Invoke<T>(ITypeShape<T> shape, object? state)
            => MessageFormatterProgressTracker.CanDeserialize(shape.Type) || MessageFormatterProgressTracker.CanSerialize(shape.Type) ? new ProgressConverter<T>() :
               TrackerHelpers.IsIAsyncEnumerable(shape.Type) ? ActivateAssociatedType<MessagePackConverter<T>>(shape, typeof(AsyncEnumerableConverter<>)) :
               TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(shape.Type) is Type iface ? ActivateAssociatedType<MessagePackConverter<T>>(shape, typeof(AsyncEnumerableConverter<>)) :
               MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(shape, DefaultRpcMarshalableProxyOptions, out JsonRpcProxyOptions? proxyOptions, out JsonRpcTargetOptions? targetOptions, out RpcMarshalableAttribute? attribute) ? new RpcMarshalableConverter<T>(shape, proxyOptions, targetOptions, attribute) :
               typeof(Exception).IsAssignableFrom(shape.Type) ? new ExceptionConverter<T>() :
               null;
    }

    private class NonDefaultConstructorVisitor<T1, T2, T3> : TypeShapeVisitor
    {
        internal static readonly NonDefaultConstructorVisitor<T1, T2, T3> Instance = new();

        private NonDefaultConstructorVisitor()
        {
        }

        public override object? VisitConstructor<TDeclaringType, TArgumentState>(IConstructorShape<TDeclaringType, TArgumentState> constructorShape, object? state = null)
        {
            Func<TArgumentState> argStateCtor = constructorShape.GetArgumentStateConstructor();
            Constructor<TArgumentState, TDeclaringType> ctor = constructorShape.GetParameterizedConstructor();
            if (constructorShape.Parameters.Count != 3 ||
                constructorShape.Parameters[0].ParameterType.Type != typeof(T1) ||
                constructorShape.Parameters[1].ParameterType.Type != typeof(T2) ||
                constructorShape.Parameters[2].ParameterType.Type != typeof(T3))
            {
                throw new InvalidOperationException("Unexpected constructor parameter types.");
            }

            var setter1 = (Setter<TArgumentState, T1>)constructorShape.Parameters[0].Accept(this)!;
            var setter2 = (Setter<TArgumentState, T2>)constructorShape.Parameters[1].Accept(this)!;
            var setter3 = (Setter<TArgumentState, T3>)constructorShape.Parameters[2].Accept(this)!;

            Func<T1, T2, T3, TDeclaringType> func = (p1, p2, p3) =>
            {
                TArgumentState state = argStateCtor();
                setter1(ref state, p1);
                setter2(ref state, p2);
                setter3(ref state, p3);
                return ctor(ref state);
            };

            return func;
        }

        public override object? VisitParameter<TArgumentState, TParameterType>(IParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null) => parameterShape.GetSetter();
    }

    [GenerateShapeFor<object>] // required because we use InvokeAsync<object> as filler for no return value.
    [GenerateShapeFor<string>]
    [GenerateShapeFor<TraceParent>]
    [GenerateShapeFor<RequestId>]
    [GenerateShapeFor<JsonRpcMessage>]
    [GenerateShapeFor<CommonErrorData>]
    [GenerateShapeFor<RawMessagePack>]
    [GenerateShapeFor<IDictionary>]
    [GenerateShapeFor<IDisposable>]
    [GenerateShapeFor<Exception>]
    [GenerateShapeFor<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>]
    private partial class Witness;
}
