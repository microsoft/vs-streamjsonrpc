// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
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
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;
using PolyType.ReflectionProvider;
using PolyType.SourceGenerator;
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
    /// A cache of property names to declared property types, indexed by their containing parameter object type.
    /// </summary>
    /// <remarks>
    /// All access to this field should be while holding a lock on this member's value.
    /// </remarks>
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Type>> ParameterObjectPropertyTypes = [];

    /// <summary>
    /// The serializer context to use for top-level RPC messages.
    /// </summary>
    private readonly FormatterProfile rpcProfile;

    private readonly ProgressConverterResolver progressConverterResolver;

    private readonly AsyncEnumerableConverterResolver asyncEnumerableConverterResolver;

    private readonly PipeConverterResolver pipeConverterResolver;

    private readonly MessagePackExceptionConverterResolver exceptionResolver;

    private readonly ToStringHelper serializationToStringHelper = new();

    private readonly ToStringHelper deserializationToStringHelper = new();

    /// <summary>
    /// The serializer to use for user data (e.g. arguments, return values and errors).
    /// </summary>
    private FormatterProfile userDataProfile;

    /// <summary>
    /// Initializes a new instance of the <see cref="NerdbankMessagePackFormatter"/> class.
    /// </summary>
    public NerdbankMessagePackFormatter()
    {
        // Set up initial options for our own message types.
        MessagePackSerializer serializer = new()
        {
            InternStrings = true,
            SerializeDefaultValues = true,
        };

        serializer.RegisterConverter(RequestIdConverter.Instance);
        serializer.RegisterConverter(new JsonRpcMessageConverter(this));
        serializer.RegisterConverter(new JsonRpcRequestConverter(this));
        serializer.RegisterConverter(new JsonRpcResultConverter(this));
        serializer.RegisterConverter(new JsonRpcErrorConverter(this));
        serializer.RegisterConverter(new JsonRpcErrorDetailConverter(this));
        serializer.RegisterConverter(new TraceParentConverter());

        this.rpcProfile = new FormatterProfile(serializer, ShapeProvider_StreamJsonRpc.Default);

        // Create the specialized formatters/resolvers that we will inject into the chain for user data.
        this.progressConverterResolver = new ProgressConverterResolver(this);
        this.asyncEnumerableConverterResolver = new AsyncEnumerableConverterResolver(this);
        this.pipeConverterResolver = new PipeConverterResolver(this);
        this.exceptionResolver = new MessagePackExceptionConverterResolver(this);

        // Create a serializer for user data.
        MessagePackSerializer userSerializer = new()
        {
            InternStrings = true,
            SerializeDefaultValues = true,
        };

        // Add our own resolvers to fill in specialized behavior if the user doesn't provide/override it by their own resolver.
        // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
        userSerializer.RegisterConverter(RequestIdConverter.Instance);

        // We preset this one because for some protocols like IProgress<T>, tokens are passed in that we must relay exactly back to the client as an argument.
        userSerializer.RegisterConverter(RawMessagePackFormatter.Instance);
        userSerializer.RegisterConverter(EventArgsConverter.Instance);

        this.userDataProfile = new FormatterProfile(userSerializer, ReflectionTypeShapeProvider.Default);
        this.ProfileBuilder = new FormatterProfileBuilder(this, this.userDataProfile);
    }

    private interface IJsonRpcMessagePackRetention
    {
        /// <summary>
        /// Gets the original msgpack sequence that was deserialized into this message.
        /// </summary>
        /// <remarks>
        /// The buffer is only retained for a short time. If it has already been cleared, the result of this property is an empty sequence.
        /// </remarks>
        ReadOnlySequence<byte> OriginalMessagePack { get; }
    }

    /// <summary>
    /// Gets the profile builder for the formatter.
    /// </summary>
    public FormatterProfileBuilder ProfileBuilder { get; }

    /// <summary>
    /// Sets the formatter profile.
    /// </summary>
    /// <param name="profile">The formatter profile to set.</param>
    public void SetFormatterProfile(FormatterProfile profile)
    {
        Requires.NotNull(profile, nameof(profile));

        this.userDataProfile = profile;
    }

    /// <summary>
    /// Configures the serialization context for user data with the specified configuration action.
    /// </summary>
    /// <param name="configure">The action to configure the serialization context.</param>
    public void SetFormatterProfile(Func<FormatterProfileBuilder, FormatterProfile> configure)
    {
        Requires.NotNull(configure, nameof(configure));

        var builder = new FormatterProfileBuilder(this, this.userDataProfile);
        FormatterProfile profile = configure(builder);

        this.userDataProfile = profile;
    }

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
    {
        JsonRpcMessage message = this.rpcProfile.Deserialize<JsonRpcMessage>(contentBuffer);

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
        SequencePosition startingPosition = reader.Position;
        reader.Skip(context);
        SequencePosition endingPosition = reader.Position;
        return reader.Sequence.Slice(startingPosition, endingPosition);
    }

    /// <summary>
    /// Reads a string with an optimized path for the value "2.0".
    /// </summary>
    /// <param name="reader">The reader to use.</param>
    /// <returns>The decoded string.</returns>
    private static unsafe string ReadProtocolVersion(ref MessagePackReader reader)
    {
        // Recognize "2.0" since we expect it and can avoid decoding and allocating a new string for it.
        if (Version2.TryRead(ref reader))
        {
            return Version2.Value;
        }
        else
        {
            // TODO: Should throw?
            return reader.ReadString() ?? string.Empty;
        }
    }

    /// <summary>
    /// Writes the JSON-RPC version property name and value in a highly optimized way.
    /// </summary>
    private static void WriteProtocolVersionPropertyAndValue(ref MessagePackWriter writer, string version)
    {
        writer.WriteRaw(VersionPropertyName.MsgPack.Span);
        writer.WriteRaw(Version2.MsgPack.Span);
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

    private class RequestIdConverter : MessagePackConverter<RequestId>
    {
        internal static readonly RequestIdConverter Instance = new();

        private RequestIdConverter()
        {
        }

        public override RequestId Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            if (reader.NextMessagePackType == MessagePackType.Integer)
            {
                return new RequestId(reader.ReadInt64());
            }
            else
            {
                // Do *not* read as an interned string here because this ID should be unique.
                return new RequestId(reader.ReadString());
            }
        }

        public override void Write(ref MessagePackWriter writer, in RequestId value, SerializationContext context)
        {
            context.DepthStep();

            if (value.Number.HasValue)
            {
                writer.Write(value.Number.Value);
            }
            else
            {
                writer.Write(value.String);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => JsonNode.Parse("""
        {
            "type": ["string", { "type": "integer", "format": "int64" }]
        }
        """)?.AsObject();
    }

    private class RawMessagePackFormatter : MessagePackConverter<RawMessagePack>
    {
        internal static readonly RawMessagePackFormatter Instance = new();

        private RawMessagePackFormatter()
        {
        }

        public override RawMessagePack Read(ref MessagePackReader reader, SerializationContext context)
        {
            return new RawMessagePack(reader.ReadRaw(context));
        }

        public override void Write(ref MessagePackWriter writer, in RawMessagePack value, SerializationContext context)
        {
            writer.WriteRaw(value);
        }
    }

    private class ProgressConverterResolver
    {
        private readonly NerdbankMessagePackFormatter mainFormatter;

        internal ProgressConverterResolver(NerdbankMessagePackFormatter formatter)
        {
            this.mainFormatter = formatter;
        }

        public MessagePackConverter<T> GetConverter<T>()
        {
            MessagePackConverter<T>? converter = default;

            if (MessageFormatterProgressTracker.CanDeserialize(typeof(T)))
            {
                converter = new PreciseTypeConverter<T>(this.mainFormatter);
            }
            else if (MessageFormatterProgressTracker.CanSerialize(typeof(T)))
            {
                converter = new ProgressClientConverter<T>(this.mainFormatter);
            }

            // TODO: Improve Exception
            return converter ?? throw new NotSupportedException();
        }

        /// <summary>
        /// Converts an instance of <see cref="IProgress{T}"/> to a progress token.
        /// </summary>
        private class ProgressClientConverter<TClass> : MessagePackConverter<TClass>
        {
            private readonly NerdbankMessagePackFormatter formatter;

            internal ProgressClientConverter(NerdbankMessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public override TClass Read(ref MessagePackReader reader, SerializationContext context)
            {
                throw new NotSupportedException("This formatter only serializes IProgress<T> instances.");
            }

            public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                context.DepthStep();

                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(ProgressClientConverter<TClass>));
            }
        }

        /// <summary>
        /// Converts a progress token to an <see cref="IProgress{T}"/> or an <see cref="IProgress{T}"/> into a token.
        /// </summary>
        private class PreciseTypeConverter<TClass> : MessagePackConverter<TClass>
        {
            private readonly NerdbankMessagePackFormatter formatter;

            internal PreciseTypeConverter(NerdbankMessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            [return: MaybeNull]
            public override TClass? Read(ref MessagePackReader reader, SerializationContext context)
            {
                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return default!;
                }

                Assumes.NotNull(this.formatter.JsonRpc);
                RawMessagePack token = (RawMessagePack)reader.ReadRaw(context);
                bool clientRequiresNamedArgs = this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.ClientRequiresNamedArguments is true;
                return (TClass)this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.JsonRpc, token, typeof(TClass), clientRequiresNamedArgs);
            }

            public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                context.DepthStep();

                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PreciseTypeConverter<TClass>));
            }
        }
    }

    private class AsyncEnumerableConverterResolver
    {
        private readonly NerdbankMessagePackFormatter mainFormatter;

        internal AsyncEnumerableConverterResolver(NerdbankMessagePackFormatter formatter)
        {
            this.mainFormatter = formatter;
        }

        public MessagePackConverter<T> GetConverter<T>()
        {
            MessagePackConverter<T>? converter = default;

            if (TrackerHelpers<IAsyncEnumerable<int>>.IsActualInterfaceMatch(typeof(T)))
            {
                converter = (MessagePackConverter<T>?)Activator.CreateInstance(
                    typeof(PreciseTypeConverter<>).MakeGenericType(typeof(T).GenericTypeArguments[0]),
                    [this.mainFormatter]);
            }
            else if (TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(typeof(T)) is { } iface)
            {
                converter = (MessagePackConverter<T>?)Activator.CreateInstance(
                    typeof(GeneratorConverter<,>).MakeGenericType(typeof(T), iface.GenericTypeArguments[0]),
                    [this.mainFormatter]);
            }

            // TODO: Improve Exception
            return converter ?? throw new NotSupportedException();
        }

        /// <summary>
        /// Converts an enumeration token to an <see cref="IAsyncEnumerable{T}"/>
        /// or an <see cref="IAsyncEnumerable{T}"/> into an enumeration token.
        /// </summary>
#pragma warning disable CA1812
        private partial class PreciseTypeConverter<T>(NerdbankMessagePackFormatter mainFormatter) : MessagePackConverter<IAsyncEnumerable<T>>
#pragma warning restore CA1812
        {
            /// <summary>
            /// The constant "token", in its various forms.
            /// </summary>
            private static readonly MessagePackString TokenPropertyName = new(MessageFormatterEnumerableTracker.TokenPropertyName);

            /// <summary>
            /// The constant "values", in its various forms.
            /// </summary>
            private static readonly MessagePackString ValuesPropertyName = new(MessageFormatterEnumerableTracker.ValuesPropertyName);

            public override IAsyncEnumerable<T>? Read(ref MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return default;
                }

                context.DepthStep();

                RawMessagePack? token = default;
                IReadOnlyList<T>? initialElements = null;
                int propertyCount = reader.ReadMapHeader();
                for (int i = 0; i < propertyCount; i++)
                {
                    if (TokenPropertyName.TryRead(ref reader))
                    {
                        token = (RawMessagePack)reader.ReadRaw(context);
                    }
                    else if (ValuesPropertyName.TryRead(ref reader))
                    {
                        initialElements = context.GetConverter<IReadOnlyList<T>>(context.TypeShapeProvider).Read(ref reader, context);
                    }
                    else
                    {
                        reader.Skip(context);
                    }
                }

                return mainFormatter.EnumerableTracker.CreateEnumerableProxy(token.HasValue ? (object)token : null, initialElements);
            }

            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to helper method")]
            public override void Write(ref MessagePackWriter writer, in IAsyncEnumerable<T>? value, SerializationContext context)
            {
                context.DepthStep();
                Serialize_Shared(mainFormatter, ref writer, value, context);
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PreciseTypeConverter<T>));
            }

            internal static void Serialize_Shared(NerdbankMessagePackFormatter mainFormatter, ref MessagePackWriter writer, IAsyncEnumerable<T>? value, SerializationContext context)
            {
                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    (IReadOnlyList<T> elements, bool finished) = value.TearOffPrefetchedElements();
                    long token = mainFormatter.EnumerableTracker.GetToken(value);

                    int propertyCount = 0;
                    if (elements.Count > 0)
                    {
                        propertyCount++;
                    }

                    if (!finished)
                    {
                        propertyCount++;
                    }

                    writer.WriteMapHeader(propertyCount);

                    if (!finished)
                    {
                        writer.WriteRaw(TokenPropertyName.MsgPack.Span);
                        writer.Write(token);
                    }

                    if (elements.Count > 0)
                    {
                        writer.WriteRaw(ValuesPropertyName.MsgPack.Span);
                        context.GetConverter<IReadOnlyList<T>>(context.TypeShapeProvider).Write(ref writer, elements, context);
                    }
                }
            }
        }

        /// <summary>
        /// Converts an instance of <see cref="IAsyncEnumerable{T}"/> to an enumeration token.
        /// </summary>
#pragma warning disable CA1812
        private class GeneratorConverter<TClass, TElement>(NerdbankMessagePackFormatter mainFormatter) : MessagePackConverter<TClass>
            where TClass : IAsyncEnumerable<TElement>
#pragma warning restore CA1812
        {
            public override TClass Read(ref MessagePackReader reader, SerializationContext context)
            {
                throw new NotSupportedException();
            }

            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to helper method")]
            public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                context.DepthStep();
                PreciseTypeConverter<TElement>.Serialize_Shared(mainFormatter, ref writer, value, context);
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(GeneratorConverter<TClass, TElement>));
            }
        }
    }

    private class PipeConverterResolver
    {
        private readonly NerdbankMessagePackFormatter mainFormatter;

        internal PipeConverterResolver(NerdbankMessagePackFormatter formatter)
        {
            this.mainFormatter = formatter;
        }

        public MessagePackConverter<T> GetConverter<T>()
        {
            MessagePackConverter<T>? converter = default;

            if (typeof(IDuplexPipe).IsAssignableFrom(typeof(T)))
            {
                converter = (MessagePackConverter<T>?)Activator.CreateInstance(typeof(DuplexPipeConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
            }
            else if (typeof(PipeReader).IsAssignableFrom(typeof(T)))
            {
                converter = (MessagePackConverter<T>?)Activator.CreateInstance(typeof(PipeReaderConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
            }
            else if (typeof(PipeWriter).IsAssignableFrom(typeof(T)))
            {
                converter = (MessagePackConverter<T>?)Activator.CreateInstance(typeof(PipeWriterConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
            }
            else if (typeof(Stream).IsAssignableFrom(typeof(T)))
            {
                converter = (MessagePackConverter<T>?)Activator.CreateInstance(typeof(StreamConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
            }

            // TODO: Improve Exception
            return converter ?? throw new NotSupportedException();
        }

#pragma warning disable CA1812
        private class DuplexPipeConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : class, IDuplexPipe
#pragma warning restore CA1812
        {
            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
            {
                context.DepthStep();

                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(DuplexPipeConverter<T>));
            }
        }

#pragma warning disable CA1812
        private class PipeReaderConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : PipeReader
#pragma warning restore CA1812
        {
            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipeReader(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
            {
                context.DepthStep();
                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PipeReaderConverter<T>));
            }
        }

#pragma warning disable CA1812
        private class PipeWriterConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : PipeWriter
#pragma warning restore CA1812
        {
            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipeWriter(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
            {
                context.DepthStep();
                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PipeWriterConverter<T>));
            }
        }

#pragma warning disable CA1812
        private class StreamConverter<T> : MessagePackConverter<T>
            where T : Stream
#pragma warning restore CA1812
        {
            private readonly NerdbankMessagePackFormatter formatter;

            public StreamConverter(NerdbankMessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)this.formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64()).AsStream();
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
            {
                context.DepthStep();

                if (this.formatter.DuplexPipeTracker.GetULongToken(value?.UsePipe()) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(StreamConverter<T>));
            }
        }
    }

#pragma warning disable CA1812
    private class RpcMarshalableConverter<T>(
        NerdbankMessagePackFormatter formatter,
        JsonRpcProxyOptions proxyOptions,
        JsonRpcTargetOptions targetOptions,
        RpcMarshalableAttribute rpcMarshalableAttribute) : MessagePackConverter<T>
        where T : class
#pragma warning restore CA1812
    {
        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Reader is passed to rpc context")]
        public override T? Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            MessageFormatterRpcMarshaledContextTracker.MarshalToken? token = formatter
                .rpcProfile
                .Deserialize<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>(
                    ref reader,
                    context.CancellationToken);

            return token.HasValue ? (T?)formatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, proxyOptions) : null;
        }

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to rpc context")]
        public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
        {
            context.DepthStep();

            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = formatter.RpcMarshaledContextTracker.GetToken(value, targetOptions, typeof(T), rpcMarshalableAttribute);
                formatter.rpcProfile.Serialize(ref writer, token, context.CancellationToken);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(RpcMarshalableConverter<T>));
        }
    }

    /// <summary>
    /// Manages serialization of any <see cref="Exception"/>-derived type that follows standard <see cref="SerializableAttribute"/> rules.
    /// </summary>
    /// <remarks>
    /// A serializable class will:
    /// 1. Derive from <see cref="Exception"/>
    /// 2. Be attributed with <see cref="SerializableAttribute"/>
    /// 3. Declare a constructor with a signature of (<see cref="SerializationInfo"/>, <see cref="StreamingContext"/>).
    /// </remarks>
    private class MessagePackExceptionConverterResolver
    {
        /// <summary>
        /// Tracks recursion count while serializing or deserializing an exception.
        /// </summary>
        /// <devremarks>
        /// This is placed here (<em>outside</em> the generic <see cref="ExceptionConverter{T}"/> class)
        /// so that it's one counter shared across all exception types that may be serialized or deserialized.
        /// </devremarks>
        private static ThreadLocal<int> exceptionRecursionCounter = new();

        private readonly object[] formatterActivationArgs;

        internal MessagePackExceptionConverterResolver(NerdbankMessagePackFormatter formatter)
        {
            this.formatterActivationArgs = new object[] { formatter };
        }

        public MessagePackConverter<T> GetConverter<T>()
        {
            MessagePackConverter<T>? formatter = null;
            if (typeof(Exception).IsAssignableFrom(typeof(T)) && typeof(T).GetCustomAttribute<SerializableAttribute>() is object)
            {
                formatter = (MessagePackConverter<T>)Activator.CreateInstance(typeof(ExceptionConverter<>).MakeGenericType(typeof(T)), this.formatterActivationArgs)!;
            }

            // TODO: Improve Exception
            return formatter ?? throw new NotSupportedException();
        }

#pragma warning disable CA1812
        private partial class ExceptionConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : Exception
#pragma warning restore CA1812
        {
            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                Assumes.NotNull(formatter.JsonRpc);

                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return null;
                }

                // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
                // Each exception in the russian doll is a new serialization job from its perspective.
                exceptionRecursionCounter.Value++;
                try
                {
                    if (exceptionRecursionCounter.Value > formatter.JsonRpc.ExceptionOptions.RecursionLimit)
                    {
                        // Exception recursion has gone too deep. Skip this value and return null as if there were no inner exception.
                        // Note that in skipping, the parser may use recursion internally and may still throw if its own limits are exceeded.
                        reader.Skip(context);
                        return null;
                    }

                    // TODO: Is this the right context?
                    var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(formatter.rpcProfile));
                    int memberCount = reader.ReadMapHeader();
                    for (int i = 0; i < memberCount; i++)
                    {
                        string? name = context.GetConverter<string>(context.TypeShapeProvider).Read(ref reader, context)
                            ?? throw new MessagePackSerializationException(Resources.UnexpectedNullValueInMap);

                        // SerializationInfo.GetValue(string, typeof(object)) does not call our formatter,
                        // so the caller will get a boxed RawMessagePack struct in that case.
                        // Although we can't do much about *that* in general, we can at least ensure that null values
                        // are represented as null instead of this boxed struct.
                        var value = reader.TryReadNil() ? null : (object)reader.ReadRaw(context);

                        info.AddSafeValue(name, value);
                    }

                    return ExceptionSerializationHelpers.Deserialize<T>(formatter.JsonRpc, info, formatter.JsonRpc.TraceSource);
                }
                finally
                {
                    exceptionRecursionCounter.Value--;
                }
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
            {
                context.DepthStep();

                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                exceptionRecursionCounter.Value++;
                try
                {
                    if (exceptionRecursionCounter.Value > formatter.JsonRpc?.ExceptionOptions.RecursionLimit)
                    {
                        // Exception recursion has gone too deep. Skip this value and write null as if there were no inner exception.
                        writer.WriteNil();
                        return;
                    }

                    // TODO: Is this the right context?
                    var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(formatter.rpcProfile));
                    ExceptionSerializationHelpers.Serialize(value, info);
                    writer.WriteMapHeader(info.GetSafeMemberCount());
                    foreach (SerializationEntry element in info.GetSafeMembers())
                    {
                        writer.Write(element.Name);
                        formatter.rpcProfile.SerializeObject(
                            ref writer,
                            element.Value,
                            element.ObjectType,
                            context.CancellationToken);
                    }
                }
                finally
                {
                    exceptionRecursionCounter.Value--;
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(ExceptionConverter<T>));
            }
        }
    }

    [GenerateShape<JsonRpcMessage>]
    private partial class JsonRpcMessageConverter : MessagePackConverter<JsonRpcMessage>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcMessageConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

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
            }

            throw new UnrecognizedJsonRpcMessageException();
        }

        public override void Write(ref MessagePackWriter writer, in JsonRpcMessage? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            context.DepthStep();

            using (this.formatter.TrackSerialization(value))
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

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return base.GetJsonSchema(context, typeShape);
        }
    }

    private partial class JsonRpcRequestConverter : MessagePackConverter<Protocol.JsonRpcRequest>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcRequestConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcRequest? Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            var result = new JsonRpcRequest(this.formatter)
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
                            result.MsgPackPositionalArguments = Array.Empty<ReadOnlySequence<byte>>();
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
                result.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataProfile, topLevelProperties);
            }

            this.formatter.TryHandleSpecialIncomingMessage(result);

            return result;
        }

        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcRequest? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

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
                writer.WriteRaw(IdPropertyName.MsgPack.Span);
                context.GetConverter<RequestId>(context.TypeShapeProvider)
                    .Write(ref writer, value.RequestId, context);
            }

            writer.WriteRaw(MethodPropertyName.MsgPack.Span);
            writer.Write(value.Method);

            writer.WriteRaw(ParamsPropertyName.MsgPack.Span);
            if (value.ArgumentsList is not null)
            {
                writer.WriteArrayHeader(value.ArgumentsList.Count);

                for (int i = 0; i < value.ArgumentsList.Count; i++)
                {
                    object? arg = value.ArgumentsList[i];

                    if (value.ArgumentListDeclaredTypes is null)
                    {
                        this.formatter.userDataProfile.SerializeObject(
                            ref writer,
                            arg,
                            context.CancellationToken);
                    }
                    else
                    {
                        this.formatter.userDataProfile.SerializeObject(
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
                        this.formatter.userDataProfile.SerializeObject(
                            ref writer,
                            entry.Value,
                            context.CancellationToken);
                    }
                    else
                    {
                        Type argType = value.NamedArgumentDeclaredTypes[entry.Key];
                        this.formatter.userDataProfile.SerializeObject(
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
                writer.WriteRaw(TraceParentPropertyName.MsgPack.Span);
                context.GetConverter<TraceParent>(context.TypeShapeProvider)
                    .Write(ref writer, new TraceParent(value.TraceParent), context);

                if (value.TraceState?.Length > 0)
                {
                    writer.WriteRaw(TraceStatePropertyName.MsgPack.Span);
                    WriteTraceState(ref writer, value.TraceState);
                }
            }

            topLevelPropertyBag?.WriteProperties(ref writer);
        }

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

    private partial class JsonRpcResultConverter : MessagePackConverter<Protocol.JsonRpcResult>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcResultConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcResult Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            var result = new JsonRpcResult(this.formatter, this.formatter.userDataProfile)
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
                result.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataProfile, topLevelProperties);
            }

            return result;
        }

        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcResult? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            context.DepthStep();

            var topLevelPropertyBagMessage = value as IMessageWithTopLevelPropertyBag;

            int mapElementCount = 3;
            mapElementCount += (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            writer.WriteRaw(IdPropertyName.MsgPack.Span);
            context.GetConverter<RequestId>(context.TypeShapeProvider).Write(ref writer, value.RequestId, context);

            writer.WriteRaw(ResultPropertyName.MsgPack.Span);

            if (value.Result is null)
            {
                writer.WriteNil();
            }

            if (value.ResultDeclaredType is not null && value.ResultDeclaredType != typeof(void))
            {
                this.formatter.userDataProfile.SerializeObject(ref writer, value.Result, value.ResultDeclaredType, context.CancellationToken);
            }
            else
            {
                this.formatter.userDataProfile.SerializeObject(ref writer, value.Result, context.CancellationToken);
            }

            (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.WriteProperties(ref writer);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcResultConverter));
        }
    }

    private partial class JsonRpcErrorConverter : MessagePackConverter<Protocol.JsonRpcError>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcErrorConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcError Read(ref MessagePackReader reader, SerializationContext context)
        {
            var error = new JsonRpcError(this.formatter.rpcProfile)
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
                error.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataProfile, topLevelProperties);
            }

            return error;
        }

        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcError? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            var topLevelPropertyBag = (TopLevelPropertyBag?)(value as IMessageWithTopLevelPropertyBag)?.TopLevelPropertyBag;

            context.DepthStep();
            int mapElementCount = 3;
            mapElementCount += topLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            writer.WriteRaw(IdPropertyName.MsgPack.Span);
            context.GetConverter<RequestId>(context.TypeShapeProvider)
                .Write(ref writer, value.RequestId, context);

            writer.WriteRaw(ErrorPropertyName.MsgPack.Span);
            context.GetConverter<Protocol.JsonRpcError.ErrorDetail>(context.TypeShapeProvider)
                .Write(ref writer, value.Error, context);

            topLevelPropertyBag?.WriteProperties(ref writer);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcErrorConverter));
        }
    }

    private partial class JsonRpcErrorDetailConverter : MessagePackConverter<Protocol.JsonRpcError.ErrorDetail>
    {
        private static readonly MessagePackString CodePropertyName = new("code");
        private static readonly MessagePackString MessagePropertyName = new("message");
        private static readonly MessagePackString DataPropertyName = new("data");

        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcErrorDetailConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcError.ErrorDetail Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            var result = new JsonRpcError.ErrorDetail(this.formatter.userDataProfile);

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

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to user data context")]
        public override void Write(ref MessagePackWriter writer, in Protocol.JsonRpcError.ErrorDetail? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            context.DepthStep();

            writer.WriteMapHeader(3);

            writer.WriteRaw(CodePropertyName.MsgPack.Span);
            context.GetConverter<JsonRpcErrorCode>(context.TypeShapeProvider)
                .Write(ref writer, value.Code, context);

            writer.WriteRaw(MessagePropertyName.MsgPack.Span);
            writer.Write(value.Message);

            writer.WriteRaw(DataPropertyName.MsgPack.Span);
            this.formatter.userDataProfile.Serialize(ref writer, value.Data, context.CancellationToken);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcErrorDetailConverter));
        }
    }

    /// <summary>
    /// Enables formatting the default/empty <see cref="EventArgs"/> class.
    /// </summary>
    private class EventArgsConverter : MessagePackConverter<EventArgs>
    {
        internal static readonly EventArgsConverter Instance = new();

        private EventArgsConverter()
        {
        }

        /// <inheritdoc/>
        public override void Write(ref MessagePackWriter writer, in EventArgs? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));
            context.DepthStep();
            writer.WriteMapHeader(0);
        }

        /// <inheritdoc/>
        public override EventArgs Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();
            reader.Skip(context);
            return EventArgs.Empty;
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(EventArgsConverter));
        }
    }

    private class TraceParentConverter : MessagePackConverter<TraceParent>
    {
        public unsafe override TraceParent Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            if (reader.ReadArrayHeader() != 2)
            {
                throw new NotSupportedException("Unexpected array length.");
            }

            var result = default(TraceParent);
            result.Version = reader.ReadByte();
            if (result.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + result.Version + " is not supported.");
            }

            if (reader.ReadArrayHeader() != 3)
            {
                throw new NotSupportedException("Unexpected array length in version-format.");
            }

            ReadOnlySequence<byte> bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected traceid not found.");
            bytes.CopyTo(new Span<byte>(result.TraceId, TraceParent.TraceIdByteCount));

            bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected parentid not found.");
            bytes.CopyTo(new Span<byte>(result.ParentId, TraceParent.ParentIdByteCount));

            result.Flags = (TraceParent.TraceFlags)reader.ReadByte();

            return result;
        }

        public unsafe override void Write(ref MessagePackWriter writer, in TraceParent value, SerializationContext context)
        {
            if (value.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + value.Version + " is not supported.");
            }

            context.DepthStep();

            writer.WriteArrayHeader(2);

            writer.Write(value.Version);

            writer.WriteArrayHeader(3);

            fixed (byte* traceId = value.TraceId)
            {
                writer.Write(new ReadOnlySpan<byte>(traceId, TraceParent.TraceIdByteCount));
            }

            fixed (byte* parentId = value.ParentId)
            {
                writer.Write(new ReadOnlySpan<byte>(parentId, TraceParent.ParentIdByteCount));
            }

            writer.Write((byte)value.Flags);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(TraceParentConverter));
        }
    }

    private class TopLevelPropertyBag : TopLevelPropertyBagBase
    {
        private readonly FormatterProfile formatterContext;
        private readonly IReadOnlyDictionary<string, ReadOnlySequence<byte>>? inboundUnknownProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an incoming message.
        /// </summary>
        /// <param name="userDataContext">The serializer options to use for this data.</param>
        /// <param name="inboundUnknownProperties">The map of unrecognized inbound properties.</param>
        internal TopLevelPropertyBag(FormatterProfile userDataContext, IReadOnlyDictionary<string, ReadOnlySequence<byte>> inboundUnknownProperties)
            : base(isOutbound: false)
        {
            this.formatterContext = userDataContext;
            this.inboundUnknownProperties = inboundUnknownProperties;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an outbound message.
        /// </summary>
        /// <param name="formatterContext">The serializer options to use for this data.</param>
        internal TopLevelPropertyBag(FormatterProfile formatterContext)
            : base(isOutbound: true)
        {
            this.formatterContext = formatterContext;
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
                    this.formatterContext.SerializeObject(ref writer, entry.Value.Value, entry.Value.DeclaredType);
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
                value = this.formatterContext.Deserialize<T>(serializedValue);
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
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.userDataProfile);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class JsonRpcRequest : JsonRpcRequestBase, IJsonRpcMessagePackRetention
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcRequest(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
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
                            typedArguments[0] = this.formatter.userDataProfile.Serializer.DeserializeObject(
                                ref reader,
                                this.formatter.userDataProfile.ShapeProvider.Resolve(parameters[0].ParameterType));

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
        private readonly FormatterProfile formatterContext;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(NerdbankMessagePackFormatter formatter, FormatterProfile serializationOptions)
        {
            this.formatter = formatter;
            this.formatterContext = serializationOptions;
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
                : this.formatterContext.Deserialize<T>(this.MsgPackResult)
                ?? throw new MessagePackSerializationException(Resources.FailureDeserializingJsonRpc);
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    this.Result = this.formatterContext.DeserializeObject(this.MsgPackResult, resultType);
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

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatterContext);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private class JsonRpcError : JsonRpcErrorBase, IJsonRpcMessagePackRetention
    {
        private readonly FormatterProfile formatterContext;

        public JsonRpcError(FormatterProfile serializerOptions)
        {
            this.formatterContext = serializerOptions;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatterContext);

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
            private readonly FormatterProfile formatterContext;

            internal ErrorDetail(FormatterProfile serializerOptions)
            {
                this.formatterContext = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
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
                    return this.formatterContext.DeserializeObject(this.MsgPackData, dataType)
                        ?? throw new MessagePackSerializationException(Resources.FailureDeserializingJsonRpc);
                }
                catch (MessagePackSerializationException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        // return MessagePackSerializer.Deserialize<object>(this.MsgPackData, this.serializerOptions.WithResolver(PrimitiveObjectResolver.Instance));
                        // TODO: Which Shape Provider to use?
                        return this.formatterContext.Deserialize<object>(this.MsgPackData);
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
}
