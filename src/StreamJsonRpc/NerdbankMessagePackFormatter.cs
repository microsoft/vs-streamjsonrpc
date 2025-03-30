﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
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
/// The MessagePack implementation used here comes from https://github.com/AArnott/Nerdbank.MessagePack.
/// </remarks>
[SuppressMessage("ApiDesign", "RS0016:Add public types and members to the declared API", Justification = "TODO: Suppressed for Development")]
public partial class NerdbankMessagePackFormatter : FormatterBase, IJsonRpcMessageFormatter, IJsonRpcFormatterTracingCallbacks, IJsonRpcMessageFactory
{
    /// <summary>
    /// The default serializer to use for user data, and a good basis for any custom values for
    /// <see cref="UserDataSerializer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This serializer is configured with <see cref="MessagePackSerializer.InternStrings"/> set to <see langword="true" />,
    /// <see cref="MessagePackSerializer.SerializeDefaultValues"/> set to <see cref="SerializeDefaultValuesPolicy.Never"/>,
    /// and various <see cref="MessagePackSerializer.Converters"/> and <see cref="MessagePackSerializer.ConverterFactories"/>.
    /// </para>
    /// <para>
    /// When deviating from this default, doing so while preserving the converters and converter factories from the default
    /// is highly recommended.
    /// It should be done once and stored in a <see langword="static" /> field and reused for the lifetime of the application
    /// to avoid repeated startup costs associated with building up the converter tree.
    /// </para>
    /// </remarks>
    public static readonly MessagePackSerializer DefaultSerializer = new()
    {
        InternStrings = true,
        SerializeDefaultValues = SerializeDefaultValuesPolicy.Never,
        ConverterFactories = [ConverterFactory.Instance],
        Converters =
            [
                GetRpcMarshalableConverter<IDisposable>(),
                PipeConverters.PipeReaderConverter<PipeReader>.DefaultInstance,
                PipeConverters.PipeWriterConverter<PipeWriter>.DefaultInstance,
                PipeConverters.StreamConverter<Stream>.DefaultInstance,
                PipeConverters.DuplexPipeConverter<IDuplexPipe>.DefaultInstance,

                // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
                RequestIdConverter.Instance,

                ExceptionConverter<Exception>.Instance,

                Nerdbank.MessagePack.Converters.PrimitivesAsObjectConverter.Instance,
            ],
    };

    /// <summary>
    /// A cache of property names to declared property types, indexed by their containing parameter object type.
    /// </summary>
    /// <remarks>
    /// All access to this field should be while holding a lock on this member's value.
    /// </remarks>
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Type>> ParameterObjectPropertyTypes = [];

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
        JsonRpcMessage message = this.envelopeSerializer.Deserialize<JsonRpcMessage>(contentBuffer, Witness.ShapeProvider)
            ?? throw new MessagePackSerializationException("Failed to deserialize JSON-RPC message.");

        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        this.deserializationToStringHelper.Activate(contentBuffer);
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
    public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
    {
        if (message is Protocol.JsonRpcRequest request
            && request.Arguments is not null
            && request.ArgumentsList is null
            && request.Arguments is not IReadOnlyDictionary<string, object?>)
        {
            // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
            // the parameters can be matched to the method we're invoking.
            if (GetParamsObjectDictionary(request.Arguments) is { } namedArgs)
            {
                request.Arguments = namedArgs.ArgumentValues;
                request.NamedArgumentDeclaredTypes = namedArgs.ArgumentTypes;
            }
        }

        var writer = new MessagePackWriter(contentBuffer);
        try
        {
            this.envelopeSerializer.Serialize(ref writer, message, Witness.ShapeProvider);
            writer.Flush();
        }
        catch (Exception ex)
        {
            throw new MessagePackSerializationException(string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcMessage, ex.GetType().Name, ex.Message), ex);
        }
    }

    /// <inheritdoc/>
    public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack
        ? MessagePackSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack)
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
        this.serializationToStringHelper.Activate(encodedMessage);
        try
        {
            tracingCallbacks?.OnMessageSerialized(message, this.serializationToStringHelper);
        }
        finally
        {
            this.serializationToStringHelper.Deactivate();
        }
    }

    internal static MessagePackConverter<T> GetRpcMarshalableConverter<T>()
        where T : class
    {
        if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(
            typeof(T),
            out JsonRpcProxyOptions? proxyOptions,
            out JsonRpcTargetOptions? targetOptions,
            out RpcMarshalableAttribute? attribute))
        {
            return (RpcMarshalableConverter<T>)Activator.CreateInstance(
                typeof(RpcMarshalableConverter<>).MakeGenericType(typeof(T)),
                proxyOptions,
                targetOptions,
                attribute)!;
        }

        // TODO: Improve Exception message.
        throw new NotSupportedException($"Type '{typeof(T).FullName}' is not supported for RPC Marshaling.");
    }

    /// <summary>
    /// Extracts a dictionary of property names and values from the specified params object.
    /// </summary>
    /// <param name="paramsObject">The params object.</param>
    /// <returns>A dictionary of argument values and another of declared argument types, or <see langword="null"/> if <paramref name="paramsObject"/> is null.</returns>
    /// <remarks>
    /// This method supports DataContractSerializer-compliant types. This includes C# anonymous types.
    /// </remarks>
    [return: NotNullIfNotNull(nameof(paramsObject))]
    private static (IReadOnlyDictionary<string, object?> ArgumentValues, IReadOnlyDictionary<string, Type> ArgumentTypes)? GetParamsObjectDictionary(object? paramsObject)
    {
        if (paramsObject is null)
        {
            return default;
        }

        // Look up the argument types dictionary if we saved it before.
        Type paramsObjectType = paramsObject.GetType();
        IReadOnlyDictionary<string, Type>? argumentTypes;
        lock (ParameterObjectPropertyTypes)
        {
            ParameterObjectPropertyTypes.TryGetValue(paramsObjectType, out argumentTypes);
        }

        // If we couldn't find a previously created argument types dictionary, create a mutable one that we'll build this time.
        Dictionary<string, Type>? mutableArgumentTypes = argumentTypes is null ? [] : null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        TypeInfo paramsTypeInfo = paramsObject.GetType().GetTypeInfo();
        bool isDataContract = paramsTypeInfo.GetCustomAttribute<DataContractAttribute>() is not null;

        BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
        if (isDataContract)
        {
            bindingFlags |= BindingFlags.NonPublic;
        }

        bool TryGetSerializationInfo(MemberInfo memberInfo, out string key)
        {
            key = memberInfo.Name;
            if (isDataContract)
            {
                DataMemberAttribute? dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                if (dataMemberAttribute is null)
                {
                    return false;
                }

                if (!dataMemberAttribute.EmitDefaultValue)
                {
                    throw new NotSupportedException($"(DataMemberAttribute.EmitDefaultValue == false) is not supported but was found on: {memberInfo.DeclaringType!.FullName}.{memberInfo.Name}.");
                }

                key = dataMemberAttribute.Name ?? memberInfo.Name;
                return true;
            }
            else
            {
                return memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() is null;
            }
        }

        foreach (PropertyInfo property in paramsTypeInfo.GetProperties(bindingFlags))
        {
            if (property.GetMethod is not null)
            {
                if (TryGetSerializationInfo(property, out string key))
                {
                    result[key] = property.GetValue(paramsObject);
                    if (mutableArgumentTypes is not null)
                    {
                        mutableArgumentTypes[key] = property.PropertyType;
                    }
                }
            }
        }

        foreach (FieldInfo field in paramsTypeInfo.GetFields(bindingFlags))
        {
            if (TryGetSerializationInfo(field, out string key))
            {
                result[key] = field.GetValue(paramsObject);
                if (mutableArgumentTypes is not null)
                {
                    mutableArgumentTypes[key] = field.FieldType;
                }
            }
        }

        // If we assembled the argument types dictionary this time, save it for next time.
        if (mutableArgumentTypes is not null)
        {
            lock (ParameterObjectPropertyTypes)
            {
                if (ParameterObjectPropertyTypes.TryGetValue(paramsObjectType, out IReadOnlyDictionary<string, Type>? lostRace))
                {
                    // Of the two, pick the winner to use ourselves so we consolidate on one and allow the GC to collect the loser sooner.
                    argumentTypes = lostRace;
                }
                else
                {
                    ParameterObjectPropertyTypes.Add(paramsObjectType, argumentTypes = mutableArgumentTypes);
                }
            }
        }

        return (result, argumentTypes!);
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

    private static unsafe void ReadUnknownProperty(ref MessagePackReader reader, in SerializationContext context, ref Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties, ReadOnlySpan<byte> stringKey)
    {
        topLevelProperties ??= new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.Ordinal);
        string name;
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
        name = Encoding.UTF8.GetString(stringKey);
#else
        fixed (byte* pStringKey = stringKey)
        {
            name = Encoding.UTF8.GetString(pStringKey, stringKey.Length);
        }
#endif
        topLevelProperties.Add(name, reader.ReadRaw(context));
    }

    private static Type NormalizeType(Type type)
    {
        if (TrackerHelpers<IProgress<int>>.FindInterfaceImplementedBy(type) is Type iface)
        {
            type = iface;
        }
        else if (TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(type) is Type iface2)
        {
            type = iface2;
        }
        else if (typeof(IDuplexPipe).IsAssignableFrom(type))
        {
            type = typeof(IDuplexPipe);
        }
        else if (typeof(PipeWriter).IsAssignableFrom(type))
        {
            type = typeof(PipeWriter);
        }
        else if (typeof(PipeReader).IsAssignableFrom(type))
        {
            type = typeof(PipeReader);
        }
        else if (typeof(Stream).IsAssignableFrom(type))
        {
            type = typeof(Stream);
        }
        else if (typeof(Exception).IsAssignableFrom(type))
        {
            type = typeof(Exception);
        }

        return type;
    }

    private ITypeShape GetUserDataShape(Type type)
    {
        type = NormalizeType(type);
        return Witness.ShapeProvider.GetShape(type) ?? this.TypeShapeProvider.Resolve(type);
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

            valueType = NormalizeType(valueType ?? value.GetType());

            // We prefer to get the shape from the user shape provider, but will fallback to our own for built-in types.
            // But if that fails too, try again with Resolve on the user shape provider so that it throws an exception explaining that the user needs to provide it.
            ITypeShape valueShape = this.TypeShapeProvider.GetShape(valueType) ?? Witness.ShapeProvider.GetShape(valueType) ?? this.TypeShapeProvider.Resolve(valueType);

#pragma warning disable NBMsgPack030 // We need to switch serializers between envelope and user data.
            this.UserDataSerializer.SerializeObject(ref writer, value, valueShape, context.CancellationToken);
#pragma warning restore NBMsgPack030 // We need to switch serializers between envelope and user data.
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
                else
                {
                    readAhead.Skip(context);
                }

                // Skip the value of the property.
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

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;

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
                    result.Method = context.GetConverter<string>(null).Read(ref reader, context);
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
                            var namedArgs = new Dictionary<string, RawMessagePack>(namedArgsCount);
                            for (int i = 0; i < namedArgsCount; i++)
                            {
                                string propertyName = context.GetConverter<string>(null).Read(ref reader, context) ?? throw new MessagePackSerializationException(Resources.UnexpectedNullValueInMap);
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
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, reader.ReadStringSpan());
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
                context.GetConverter<RequestId>(Witness.ShapeProvider).Write(ref writer, value.RequestId, context);
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
            else
            {
                writer.WriteNil();
            }

            if (value.TraceParent?.Length > 0)
            {
                writer.Write(TraceParentPropertyName);
                context.GetConverter<TraceParent>(Witness.ShapeProvider).Write(ref writer, new TraceParent(value.TraceParent), context);

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
                resultBuilder.Append(context.GetConverter<string>(null).Read(ref reader, context));
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

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;

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
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, reader.ReadStringSpan());
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

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;

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
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, reader.ReadStringSpan());
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
                ITypeShapeProvider provider = value.Data is CommonErrorData or Exception ? Witness.ShapeProvider : formatter.TypeShapeProvider;
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
        private readonly IReadOnlyDictionary<string, ReadOnlySequence<byte>>? inboundUnknownProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an incoming message.
        /// </summary>
        /// <param name="formatter">The owning formatter.</param>
        /// <param name="inboundUnknownProperties">The map of unrecognized inbound properties.</param>
        internal TopLevelPropertyBag(NerdbankMessagePackFormatter formatter, IReadOnlyDictionary<string, ReadOnlySequence<byte>> inboundUnknownProperties)
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
                    this.formatter.userDataSerializer.SerializeObject(ref writer, entry.Value.Value, this.formatter.TypeShapeProvider.Resolve(entry.Value.DeclaredType));
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

            if (this.inboundUnknownProperties.TryGetValue(name, out ReadOnlySequence<byte> serializedValue) is true)
            {
                value = this.formatter.userDataSerializer.Deserialize<T>(serializedValue, this.formatter.TypeShapeProvider);
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
                                this.formatter.TypeShapeProvider.Resolve(parameters[0].ParameterType));

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
                : formatter.userDataSerializer.Deserialize<T>(this.MsgPackResult, formatter.TypeShapeProvider)
                ?? throw new MessagePackSerializationException("Failed to deserialize result.");
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(!this.MsgPackResult.MsgPack.IsEmpty, "Result is no longer available or has already been deserialized.");

            try
            {
                using (formatter.TrackDeserialization(this))
                {
                    MessagePackReader reader = new(this.MsgPackResult);
                    this.Result = formatter.userDataSerializer.DeserializeObject(ref reader, formatter.TypeShapeProvider.Resolve(resultType));
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
                        (dataType == typeof(Exception) || dataType == typeof(CommonErrorData)) ? formatter.envelopeSerializer.DeserializeObject(ref reader, Witness.ShapeProvider.Resolve(dataType)) :
                        formatter.userDataSerializer.DeserializeObject(ref reader, formatter.TypeShapeProvider.Resolve(dataType));
                }
                catch (MessagePackSerializationException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        reader = new(this.MsgPackData);
                        return formatter.envelopeSerializer.DeserializeDynamic(ref reader);
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

    private class ConverterFactory : IMessagePackConverterFactory
    {
        internal static readonly ConverterFactory Instance = new();

        private ConverterFactory()
        {
        }

        public MessagePackConverter<T>? CreateConverter<T>()
            => MessageFormatterProgressTracker.CanDeserialize(typeof(T)) ? new FullProgressConverter<T>() :
               MessageFormatterProgressTracker.CanSerialize(typeof(T)) ? new ProgressClientConverter<T>() :
               TrackerHelpers<IAsyncEnumerable<int>>.IsActualInterfaceMatch(typeof(T)) ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(AsyncEnumerableConverters.PreciseTypeConverter<>).MakeGenericType(typeof(T).GenericTypeArguments[0]))! :
               TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(typeof(T)) is Type iface ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(AsyncEnumerableConverters.GeneratorConverter<,>).MakeGenericType(typeof(T), iface.GenericTypeArguments[0]))! :
               MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(typeof(T), out JsonRpcProxyOptions? proxyOptions, out JsonRpcTargetOptions? targetOptions, out RpcMarshalableAttribute? attribute) ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(RpcMarshalableConverter<>).MakeGenericType(typeof(T)), proxyOptions, targetOptions, attribute)! :
               typeof(IDuplexPipe).IsAssignableFrom(typeof(T)) ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(PipeConverters.DuplexPipeConverter<>).MakeGenericType(typeof(T)))! :
               typeof(PipeReader).IsAssignableFrom(typeof(T)) ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(PipeConverters.PipeReaderConverter<>).MakeGenericType(typeof(T)))! :
               typeof(PipeWriter).IsAssignableFrom(typeof(T)) ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(PipeConverters.PipeWriterConverter<>).MakeGenericType(typeof(T)))! :
               typeof(Stream).IsAssignableFrom(typeof(T)) ? (MessagePackConverter<T>)Activator.CreateInstance(typeof(PipeConverters.StreamConverter<>).MakeGenericType(typeof(T)))! :
               typeof(Exception).IsAssignableFrom(typeof(T)) ? new ExceptionConverter<T>() :
               null;
    }

    [GenerateShape<TraceParent>]
    [GenerateShape<RequestId>]
    [GenerateShape<JsonRpcMessage>]
    [GenerateShape<CommonErrorData>]
    [GenerateShape<RawMessagePack>]
    [GenerateShape<IDictionary>]
    [GenerateShape<Exception>]
    [GenerateShape<RemoteInvocationException>]
    [GenerateShape<RemoteMethodNotFoundException>]
    [GenerateShape<RemoteRpcException>]
    [GenerateShape<RemoteSerializationException>]
    [GenerateShape<MessageFormatterRpcMarshaledContextTracker.MarshalToken>]
    [GenerateShape<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>]
    private partial class Witness;
}
