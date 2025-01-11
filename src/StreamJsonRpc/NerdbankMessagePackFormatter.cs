// Copyright (c) Microsoft Corporation. All rights reserved.
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
using PolyType.ReflectionProvider;
using PolyType.SourceGenerator;
using PolyType.SourceGenModel;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// The MessagePack implementation used here comes from https://github.com/AArnott/Nerdbank.MessagePack.
/// </remarks>
[GenerateShape<TraceParent>]
[GenerateShape<IDictionary>]
[GenerateShape<Exception>]
[GenerateShape<RemoteInvocationException>]
[GenerateShape<RemoteMethodNotFoundException>]
[GenerateShape<RemoteRpcException>]
[GenerateShape<RemoteSerializationException>]
[GenerateShape<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>]
[SuppressMessage("ApiDesign", "RS0016:Add public types and members to the declared API", Justification = "TODO: Suppressed for Development")]
public partial class NerdbankMessagePackFormatter : FormatterBase, IJsonRpcMessageFormatter, IJsonRpcFormatterTracingCallbacks, IJsonRpcMessageFactory
{
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
    private readonly Profile rpcProfile;

    private readonly ToStringHelper serializationToStringHelper = new();

    private readonly ToStringHelper deserializationToStringHelper = new();

    private readonly ThreadLocal<int> exceptionRecursionCounter = new();

    /// <summary>
    /// The serializer to use for user data (e.g. arguments, return values and errors).
    /// </summary>
    private Profile userDataProfile;

    /// <summary>
    /// Initializes a new instance of the <see cref="NerdbankMessagePackFormatter"/> class.
    /// </summary>
    public NerdbankMessagePackFormatter()
    {
        KnownSubTypeMapping<Exception> exceptionSubtypeMap = new();
        exceptionSubtypeMap.Add<RemoteInvocationException>(alias: 1, ShapeProvider);
        exceptionSubtypeMap.Add<RemoteMethodNotFoundException>(alias: 2, ShapeProvider);
        exceptionSubtypeMap.Add<RemoteRpcException>(alias: 3, ShapeProvider);
        exceptionSubtypeMap.Add<RemoteSerializationException>(alias: 4, ShapeProvider);

        // Set up initial options for our own message types.
        MessagePackSerializer serializer = new()
        {
            InternStrings = true,
            SerializeDefaultValues = false,
            StartingContext = new SerializationContext()
            {
                [SerializationContextExtensions.FormatterKey] = this,
            },
        };

        serializer.RegisterKnownSubTypes(exceptionSubtypeMap);
        RegisterCommonConverters(serializer);

        this.rpcProfile = new Profile(
            Profile.ProfileSource.Internal,
            serializer,
            [
                ExoticTypeShapeProvider.Instance,
                ShapeProvider_StreamJsonRpc.Default
            ]);

        // Create a serializer for user data.
        MessagePackSerializer userSerializer = new()
        {
            InternStrings = true,
            SerializeDefaultValues = false,
            StartingContext = new SerializationContext()
            {
                [SerializationContextExtensions.FormatterKey] = this,
            },
        };

        userSerializer.RegisterKnownSubTypes(exceptionSubtypeMap);
        RegisterCommonConverters(userSerializer);

        this.userDataProfile = new Profile(
            Profile.ProfileSource.External,
            userSerializer,
            [
                ExoticTypeShapeProvider.Instance,
                ReflectionTypeShapeProvider.Default
            ]);

        this.ProfileBuilder = new Profile.Builder(this.userDataProfile);

        // Add our own resolvers to fill in specialized behavior if the user doesn't provide/override it by their own resolver.
        static void RegisterCommonConverters(MessagePackSerializer serializer)
        {
            serializer.RegisterConverter(GetRpcMarshalableConverter<IDisposable>());
            serializer.RegisterConverter(PipeConverters.PipeReaderConverter<PipeReader>.DefaultInstance);
            serializer.RegisterConverter(PipeConverters.PipeWriterConverter<PipeWriter>.DefaultInstance);
            serializer.RegisterConverter(PipeConverters.StreamConverter<Stream>.DefaultInstance);
            serializer.RegisterConverter(PipeConverters.DuplexPipeConverter<IDuplexPipe>.DefaultInstance);

            // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
            serializer.RegisterConverter(RequestIdConverter.Instance);

            // We preset this one because for some protocols like IProgress<T>, tokens are passed in that we must relay exactly back to the client as an argument.
            serializer.RegisterConverter(EventArgsConverter.Instance);
            serializer.RegisterConverter(ExceptionConverter<Exception>.Instance);
            serializer.RegisterConverter(ExceptionConverter<RemoteInvocationException>.Instance);
            serializer.RegisterConverter(ExceptionConverter<RemoteMethodNotFoundException>.Instance);
            serializer.RegisterConverter(ExceptionConverter<RemoteRpcException>.Instance);
            serializer.RegisterConverter(ExceptionConverter<RemoteSerializationException>.Instance);
        }
    }

    /// <summary>
    /// Gets the profile builder for the formatter.
    /// </summary>
    public Profile.Builder ProfileBuilder { get; }

    /// <summary>
    /// Sets the formatter profile for user data.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         For improved startup performance, use <see cref="ProfileBuilder"/>
    ///         to configure a reusable profile and set it here for each instance of this formatter.
    ///         The profile must be configured before any messages are serialized or deserialized.
    ///     </para>
    ///     <para>
    ///         If not set, a default profile is used which will resolve types using reflection emit.
    ///     </para>
    /// </remarks>
    /// <param name="profile">The formatter profile to set.</param>
    public void SetFormatterProfile(Profile profile)
    {
        Requires.NotNull(profile, nameof(profile));
        this.userDataProfile = profile.WithFormatterState(this);
    }

    /// <summary>
    /// Configures the formatter profile for user data with the specified configuration action.
    /// </summary>
    /// <remarks>
    ///     Generally prefer using <see cref="SetFormatterProfile(Profile)"/> over this method
    ///     as it is more efficient to reuse a profile across multiple instances of this formatter.
    /// </remarks>
    /// <param name="configure">The configuration action.</param>
    public void SetFormatterProfile(Action<Profile.Builder> configure)
    {
        Requires.NotNull(configure, nameof(configure));

        var builder = new Profile.Builder(this.userDataProfile);
        configure(builder);
        this.SetFormatterProfile(builder.Build());
    }

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
    {
        JsonRpcMessage message = this.rpcProfile.Deserialize<JsonRpcMessage>(contentBuffer)
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
            this.rpcProfile.Serialize(ref writer, message);
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
    Protocol.JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() => new JsonRpcError(this.rpcProfile);

    /// <inheritdoc/>
    Protocol.JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() => new JsonRpcResult(this, this.rpcProfile);

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

    private static ReadOnlySequence<byte> GetSliceForNextToken(ref MessagePackReader reader, in SerializationContext context)
    {
        SequencePosition startPosition = reader.Position;
        reader.Skip(context);
        SequencePosition endPosition = reader.Position;
        return reader.Sequence.Slice(startPosition, endPosition);
    }

    /// <summary>
    /// Reads a string with an optimized path for the value "2.0".
    /// </summary>
    /// <param name="reader">The reader to use.</param>
    /// <returns>The decoded string.</returns>
    private static string ReadProtocolVersion(ref MessagePackReader reader)
    {
        // Recognize "2.0" since we expect it and can avoid decoding and allocating a new string for it.
        if (Version2.TryRead(ref reader))
        {
            return Version2.Value;
        }
        else
        {
            // TODO: Should throw?
            return reader.ReadString() ?? throw new MessagePackSerializationException(Resources.RequiredArgumentMissing);
        }
    }

    /// <summary>
    /// Writes the JSON-RPC version property name and value in a highly optimized way.
    /// </summary>
    private static void WriteProtocolVersionPropertyAndValue(ref MessagePackWriter writer, string version)
    {
        writer.Write(VersionPropertyName);
        writer.Write(version);
    }

    private static void ReadUnknownProperty(ref MessagePackReader reader, in SerializationContext context, ref Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties, ReadOnlySpan<byte> stringKey)
    {
        topLevelProperties ??= new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.Ordinal);
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
        string name = Encoding.UTF8.GetString(stringKey);
#else
        string name = Encoding.UTF8.GetString(stringKey.ToArray());
#endif
        topLevelProperties.Add(name, GetSliceForNextToken(ref reader, context));
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
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return base.GetJsonSchema(context, typeShape);
        }
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
                OriginalMessagePack = reader.Sequence,
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
                            var positionalArgs = new ReadOnlySequence<byte>[reader.ReadArrayHeader()];
                            for (int i = 0; i < positionalArgs.Length; i++)
                            {
                                positionalArgs[i] = GetSliceForNextToken(ref reader, context);
                            }

                            result.MsgPackPositionalArguments = positionalArgs;
                            break;
                        case MessagePackType.Map:
                            int namedArgsCount = reader.ReadMapHeader();
                            var namedArgs = new Dictionary<string, ReadOnlySequence<byte>>(namedArgsCount);
                            for (int i = 0; i < namedArgsCount; i++)
                            {
                                string? propertyName = context.GetConverter<string>(null).Read(ref reader, context) ?? throw new MessagePackSerializationException(Resources.UnexpectedNullValueInMap);
                                namedArgs.Add(propertyName, GetSliceForNextToken(ref reader, context));
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

                    result.MsgPackArguments = reader.Sequence.Slice(paramsTokenStartPosition, reader.Position);
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
                result.TopLevelPropertyBag = new TopLevelPropertyBag(formatter.userDataProfile, topLevelProperties);
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
                context.GetConverter<RequestId>(context.TypeShapeProvider)
                    .Write(ref writer, value.RequestId, context);
            }

            writer.Write(MethodPropertyName);
            writer.Write(value.Method);

            writer.Write(ParamsPropertyName);
            if (value.ArgumentsList is not null)
            {
                writer.WriteArrayHeader(value.ArgumentsList.Count);

                for (int i = 0; i < value.ArgumentsList.Count; i++)
                {
                    object? arg = value.ArgumentsList[i];

                    if (value.ArgumentListDeclaredTypes is null)
                    {
                        formatter.userDataProfile.SerializeObject(
                            ref writer,
                            arg,
                            context.CancellationToken);
                    }
                    else
                    {
                        formatter.userDataProfile.SerializeObject(
                            ref writer,
                            arg,
                            value.ArgumentListDeclaredTypes[i],
                            context.CancellationToken);
                    }
                }
            }
            else if (value.NamedArguments is not null)
            {
                writer.WriteMapHeader(value.NamedArguments.Count);
                foreach (KeyValuePair<string, object?> entry in value.NamedArguments)
                {
                    writer.Write(entry.Key);

                    if (value.NamedArgumentDeclaredTypes is null)
                    {
                        formatter.userDataProfile.SerializeObject(
                            ref writer,
                            entry.Value,
                            context.CancellationToken);
                    }
                    else
                    {
                        Type argType = value.NamedArgumentDeclaredTypes[entry.Key];
                        formatter.userDataProfile.SerializeObject(
                            ref writer,
                            entry.Value,
                            argType,
                            context.CancellationToken);
                    }
                }
            }
            else
            {
                writer.WriteNil();
            }

            if (value.TraceParent?.Length > 0)
            {
                writer.Write(TraceParentPropertyName);
                formatter.rpcProfile.Serialize(ref writer, new TraceParent(value.TraceParent));

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
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcRequestConverter));
        }

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

            var result = new JsonRpcResult(formatter, formatter.userDataProfile)
            {
                OriginalMessagePack = reader.Sequence,
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
                    result.MsgPackResult = GetSliceForNextToken(ref reader, context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, reader.ReadStringSpan());
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(formatter.userDataProfile, topLevelProperties);
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
            else if (value.ResultDeclaredType is not null
                && value.ResultDeclaredType != typeof(void)
                && value.ResultDeclaredType != typeof(object))
            {
                formatter.userDataProfile.SerializeObject(ref writer, value.Result, value.ResultDeclaredType, context.CancellationToken);
            }
            else
            {
                formatter.userDataProfile.SerializeObject(ref writer, value.Result, context.CancellationToken);
            }

            (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.WriteProperties(ref writer);
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcResultConverter));
        }
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
            var error = new JsonRpcError(formatter.userDataProfile)
            {
                OriginalMessagePack = reader.Sequence,
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
                error.TopLevelPropertyBag = new TopLevelPropertyBag(formatter.userDataProfile, topLevelProperties);
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
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcErrorConverter));
        }
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

            var result = new JsonRpcError.ErrorDetail(formatter.userDataProfile);

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
                    result.MsgPackData = GetSliceForNextToken(ref reader, context);
                }
                else
                {
                    reader.Skip(context);
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
            formatter.userDataProfile.Serialize(ref writer, value.Data, context.CancellationToken);
        }

        /// <summary>
        /// Gets the JSON schema for the specified type.
        /// </summary>
        /// <param name="context">The JSON schema context.</param>
        /// <param name="typeShape">The type shape.</param>
        /// <returns>The JSON schema for the specified type.</returns>
        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcErrorDetailConverter));
        }
    }

    private class TopLevelPropertyBag : TopLevelPropertyBagBase
    {
        private readonly Profile formatterProfile;
        private readonly IReadOnlyDictionary<string, ReadOnlySequence<byte>>? inboundUnknownProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an incoming message.
        /// </summary>
        /// <param name="formatterProfile">The profile use for this data.</param>
        /// <param name="inboundUnknownProperties">The map of unrecognized inbound properties.</param>
        internal TopLevelPropertyBag(Profile formatterProfile, IReadOnlyDictionary<string, ReadOnlySequence<byte>> inboundUnknownProperties)
            : base(isOutbound: false)
        {
            this.formatterProfile = formatterProfile;
            this.inboundUnknownProperties = inboundUnknownProperties;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an outbound message.
        /// </summary>
        /// <param name="formatterProfile">The profile to use for this data.</param>
        internal TopLevelPropertyBag(Profile formatterProfile)
            : base(isOutbound: true)
        {
            this.formatterProfile = formatterProfile;
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
                    this.formatterProfile.SerializeObject(ref writer, entry.Value.Value, entry.Value.DeclaredType);
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
                value = this.formatterProfile.Deserialize<T>(serializedValue);
                return true;
            }

            return false;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class OutboundJsonRpcRequest : JsonRpcRequestBase
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal OutboundJsonRpcRequest(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.userDataProfile);
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

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        internal ReadOnlySequence<byte> MsgPackArguments { get; set; }

        internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

        internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

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
                            typedArguments[0] = this.formatter.userDataProfile.DeserializeObject(
                                ref reader,
                                parameters[0].ParameterType);

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

            ReadOnlySequence<byte> msgpackArgument = default;
            if (position >= 0 && this.MsgPackPositionalArguments?.Count > position)
            {
                msgpackArgument = this.MsgPackPositionalArguments[position];
            }
            else if (name is not null && this.MsgPackNamedArguments is not null)
            {
                this.MsgPackNamedArguments.TryGetValue(name, out msgpackArgument);
            }

            if (msgpackArgument.IsEmpty)
            {
                value = null;
                return false;
            }

            using (this.formatter.TrackDeserialization(this))
            {
                try
                {
                    value = this.formatter.userDataProfile.DeserializeObject(
                        msgpackArgument,
                        typeHint ?? typeof(object));

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

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.userDataProfile);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private partial class JsonRpcResult : JsonRpcResultBase, IJsonRpcMessagePackRetention
    {
        private readonly NerdbankMessagePackFormatter formatter;
        private readonly Profile formatterProfile;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(NerdbankMessagePackFormatter formatter, Profile formatterProfile)
        {
            this.formatter = formatter;
            this.formatterProfile = formatterProfile;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        internal ReadOnlySequence<byte> MsgPackResult { get; set; }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            return this.MsgPackResult.IsEmpty
                ? (T)this.Result!
                : this.formatterProfile.Deserialize<T>(this.MsgPackResult)
                ?? throw new MessagePackSerializationException("Failed to deserialize result.");
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    this.Result = this.formatterProfile.DeserializeObject(this.MsgPackResult, resultType);
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

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatterProfile);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class JsonRpcError : JsonRpcErrorBase, IJsonRpcMessagePackRetention
    {
        private readonly Profile formatterProfile;

        public JsonRpcError(Profile formatterProfile)
        {
            this.formatterProfile = formatterProfile;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatterProfile);

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            if (this.Error is ErrorDetail privateDetail)
            {
                privateDetail.MsgPackData = default;
            }

            this.OriginalMessagePack = default;
        }

        internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
        {
            private readonly Profile formatterProfile;

            internal ErrorDetail(Profile formatterProfile)
            {
                this.formatterProfile = formatterProfile ?? throw new ArgumentNullException(nameof(formatterProfile));
            }

            internal ReadOnlySequence<byte> MsgPackData { get; set; }

            public override object? GetData(Type dataType)
            {
                Requires.NotNull(dataType, nameof(dataType));
                if (this.MsgPackData.IsEmpty)
                {
                    return this.Data;
                }

                try
                {
                    return this.formatterProfile.DeserializeObject(this.MsgPackData, dataType)
                        ?? throw new MessagePackSerializationException(Resources.FailureDeserializingJsonRpc);
                }
                catch (MessagePackSerializationException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        // return MessagePackSerializer.Deserialize<object>(this.MsgPackData, this.serializerOptions.WithResolver(PrimitiveObjectResolver.Instance));
                        return this.formatterProfile.Deserialize<object>(this.MsgPackData);
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

    /// <summary>
    /// Ensures certain exotic types are matched to the correct MessagePackConverter.
    /// We rely on the caching in NerbdBank.MessagePackSerializer to ensure we don't create multiple instances of these shapes.
    /// </summary>
    private class ExoticTypeShapeProvider : ITypeShapeProvider
    {
        internal static readonly ExoticTypeShapeProvider Instance = new();

        public ITypeShape? GetShape(Type type)
        {
            if (typeof(PipeReader).IsAssignableFrom(type))
            {
                return new SourceGenObjectTypeShape<PipeReader>()
                {
                    IsRecordType = false,
                    IsTupleType = false,
                    Provider = this,
                };
            }

            if (typeof(PipeWriter).IsAssignableFrom(type))
            {
                return new SourceGenObjectTypeShape<PipeWriter>()
                {
                    IsRecordType = false,
                    IsTupleType = false,
                    Provider = this,
                };
            }

            if (typeof(Stream).IsAssignableFrom(type))
            {
                return new SourceGenObjectTypeShape<Stream>()
                {
                    IsRecordType = false,
                    IsTupleType = false,
                    Provider = this,
                };
            }

            if (typeof(IDuplexPipe).IsAssignableFrom(type))
            {
                return new SourceGenObjectTypeShape<IDuplexPipe>()
                {
                    IsRecordType = false,
                    IsTupleType = false,
                    Provider = this,
                };
            }

            return null;
        }
    }
}
