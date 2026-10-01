// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nerdbank.Streams;

public partial class SystemTextJsonFormatterTests : FormatterTestBase<SystemTextJsonFormatter>
{
    public SystemTextJsonFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void STJAttributesWinOverDataContractAttributesByDefault()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
        requestMessage.Method = "test";
        requestMessage.Arguments = new[] { new DCSClass { C = 1 } };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, requestMessage);

        using JsonDocument doc = JsonDocument.Parse(sequence);
        this.Logger.WriteLine(doc.RootElement.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("B").GetInt32());
    }

    [Fact]
    public void STJAttributesWinOverDataMemberWithoutDataContract()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
        requestMessage.Method = "test";
        requestMessage.Arguments = new[] { new STJClass { C = 1 } };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, requestMessage);

        using JsonDocument doc = JsonDocument.Parse(sequence);
        this.Logger.WriteLine(doc.RootElement.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("B").GetInt32());
    }

    /// <summary>
    /// Verifies cancellation parameters work when the application context omits RequestId.
    /// </summary>
    [Theory]
    [CombinatorialData]
    public void CancellationParameterUsesBuiltInMetadata(bool useResolverChain, bool readOnly, bool stringId)
    {
        Assert.Null(ApplicationJsonContext.Default.GetTypeInfo(typeof(RequestId)));
        JsonSerializerOptions options = new();
        if (useResolverChain)
        {
            options.TypeInfoResolverChain.Add(ApplicationJsonContext.Default);
        }
        else
        {
            options.TypeInfoResolver = ApplicationJsonContext.Default;
        }

        if (readOnly)
        {
            options.MakeReadOnly();
        }

        this.Formatter.JsonSerializerOptions = options;
        Assert.Equal(2, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain.Count);
        Assert.Same(ApplicationJsonContext.Default, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain[1]);
        Assert.False(this.Formatter.JsonSerializerOptions.IsReadOnly);

        this.AssertCancellationRoundtrip(stringId ? new RequestId("request") : new RequestId(42), stringId ? "\"request\"" : "42");
    }

    /// <summary>
    /// Verifies built-in metadata is available for incoming cancellation parameters.
    /// </summary>
    [Fact]
    public void DeserializationUsesBuiltInMetadata()
    {
        this.Formatter.JsonSerializerOptions = new() { TypeInfoResolver = ApplicationJsonContext.Default };
        byte[] json = """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":42}}"""u8.ToArray();
        JsonRpcRequest request = Assert.IsAssignableFrom<JsonRpcRequest>(this.Formatter.Deserialize(new ReadOnlySequence<byte>(json)));

        Assert.True(request.TryGetArgumentByNameOrIndex("id", 0, typeof(RequestId), out object? actual));
        Assert.Equal(new RequestId(42), Assert.IsType<RequestId>(actual));
    }

    /// <summary>
    /// Verifies results and property bags use built-in metadata.
    /// </summary>
    [Fact]
    public void ResultAndTopLevelPropertyUseBuiltInMetadata()
    {
        this.Formatter.JsonSerializerOptions = new() { TypeInfoResolver = ApplicationJsonContext.Default };
        IJsonRpcMessageFactory factory = this.Formatter;
        JsonRpcResult result = factory.CreateResultMessage();
        result.Result = new RequestId(42);
        Assert.True(result.TrySetTopLevelProperty("extra", new RequestId("value")));

        JsonRpcResult deserialized = this.Roundtrip(result);
        Assert.Equal(new RequestId(42), deserialized.GetResult<RequestId>());
        Assert.True(deserialized.TryGetTopLevelProperty("extra", out RequestId extra));
        Assert.Equal(new RequestId("value"), extra);
    }

    /// <summary>
    /// Verifies callers can override built-in serialization after assigning options.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomRequestIdSerializationTakesPrecedence(bool useResolver)
    {
        this.Formatter.JsonSerializerOptions = new() { TypeInfoResolver = ApplicationJsonContext.Default };
        if (useResolver)
        {
            this.Formatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, new CustomRequestIdResolver());
        }
        else
        {
            this.Formatter.JsonSerializerOptions.Converters.Insert(0, new OffsetRequestIdConverter());
        }

        this.AssertCancellationRoundtrip(new RequestId(42), "43");
    }

    /// <summary>
    /// Verifies built-in metadata takes precedence over previously configured resolvers.
    /// </summary>
    [Fact]
    public void BuiltInMetadataPrecedesExistingResolvers()
    {
        CustomRequestIdResolver resolver = new();
        this.Formatter.JsonSerializerOptions = new() { TypeInfoResolver = resolver };

        Assert.Equal(2, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain.Count);
        Assert.Same(resolver, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain[1]);
        this.AssertCancellationRoundtrip(new RequestId(42), "42");
    }

    /// <summary>
    /// Verifies the default reflection resolver follows the built-in source-generated metadata.
    /// </summary>
    [Fact]
    public void BuiltInMetadataPrecedesDefaultReflection()
    {
        JsonSerializerOptions options = this.Formatter.JsonSerializerOptions;
        Assert.Equal(2, options.TypeInfoResolverChain.Count);
        Assert.IsNotType<DefaultJsonTypeInfoResolver>(options.TypeInfoResolverChain[0]);
        Assert.NotNull(options.TypeInfoResolverChain[0].GetTypeInfo(typeof(RequestId), options));
        Assert.IsType<DefaultJsonTypeInfoResolver>(options.TypeInfoResolverChain[1]);
    }

    /// <summary>
    /// Verifies replacing used options prepends built-in metadata without duplicating it.
    /// </summary>
    [Fact]
    public void OptionsReplacementPrependsBuiltInResolver()
    {
        this.Formatter.JsonSerializerOptions = new() { TypeInfoResolver = ApplicationJsonContext.Default };
        IJsonTypeInfoResolver builtInResolver = this.Formatter.JsonSerializerOptions.TypeInfoResolverChain[0];
        this.AssertCancellationRoundtrip(new RequestId(42), "42");
        Assert.True(this.Formatter.JsonSerializerOptions.IsReadOnly);

        JsonSerializerOptions replacement = new(this.Formatter.JsonSerializerOptions);
        CustomRequestIdResolver resolver = new();
        replacement.TypeInfoResolverChain.Insert(0, resolver);
        this.Formatter.JsonSerializerOptions = replacement;

        this.AssertCancellationRoundtrip(new RequestId(42), "42");
        Assert.Equal(3, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain.Count);
        Assert.Same(builtInResolver, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain[0]);
        Assert.Same(resolver, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain[1]);
        Assert.Same(resolver, replacement.TypeInfoResolverChain[0]);
        Assert.Same(builtInResolver, replacement.TypeInfoResolverChain[1]);
    }

    /// <summary>
    /// Verifies automatic registration does not mutate an options instance passed to the setter.
    /// </summary>
    [Fact]
    public void CallerOptionsRemainUnchanged()
    {
        JsonSerializerOptions options = new() { TypeInfoResolver = ApplicationJsonContext.Default };
        this.Formatter.JsonSerializerOptions = options;
        this.AssertCancellationRoundtrip(new RequestId(42), "42");

        Assert.NotSame(options, this.Formatter.JsonSerializerOptions);
        Assert.False(options.IsReadOnly);
        Assert.Empty(options.Converters);
        Assert.Same(ApplicationJsonContext.Default, Assert.Single(options.TypeInfoResolverChain));
    }

    /// <summary>
    /// Verifies built-in metadata does not introduce reflection fallback for application types.
    /// </summary>
    [Fact]
    public void MissingApplicationMetadataStillFails()
    {
        this.Formatter.JsonSerializerOptions = new() { TypeInfoResolver = ApplicationJsonContext.Default };
        IJsonRpcMessageFactory factory = this.Formatter;
        JsonRpcRequest request = factory.CreateRequestMessage();
        request.Method = "test";
        request.Arguments = new[] { new CustomType { Age = 42 } };

        using Sequence<byte> sequence = new();
        JsonException exception = Assert.Throws<JsonException>(() => this.Formatter.Serialize(sequence, request));
        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    /// <summary>
    /// Verifies replacing the resolver through the getter overrides the built-in registration.
    /// </summary>
    [Fact]
    public void ResolverReplacementRemovesBuiltInMetadata()
    {
        this.Formatter.JsonSerializerOptions.TypeInfoResolver = ApplicationJsonContext.Default;
        IJsonRpcMessageFactory factory = this.Formatter;
        JsonRpcRequest request = factory.CreateRequestMessage();
        request.Method = "$/cancelRequest";
        request.Arguments = new Dictionary<string, object?> { ["id"] = new RequestId(42) };

        using Sequence<byte> sequence = new();
        JsonException exception = Assert.Throws<JsonException>(() => this.Formatter.Serialize(sequence, request));
        Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Same(ApplicationJsonContext.Default, Assert.Single(this.Formatter.JsonSerializerOptions.TypeInfoResolverChain));
    }

    protected override SystemTextJsonFormatter CreateFormatter() => new();

    private void AssertCancellationRoundtrip(RequestId id, string expectedJson)
    {
        IJsonRpcMessageFactory factory = this.Formatter;
        JsonRpcRequest request = factory.CreateRequestMessage();
        request.Method = "$/cancelRequest";
        request.Arguments = new Dictionary<string, object?> { ["id"] = id };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, request);
        using JsonDocument document = JsonDocument.Parse(sequence);
        Assert.Equal(expectedJson, document.RootElement.GetProperty("params").GetProperty("id").GetRawText());

        JsonRpcRequest deserialized = Assert.IsAssignableFrom<JsonRpcRequest>(this.Formatter.Deserialize(sequence));
        Assert.True(deserialized.TryGetArgumentByNameOrIndex("id", 0, typeof(RequestId), out object? actual));
        Assert.Equal(id, Assert.IsType<RequestId>(actual));
    }

    [DataContract]
    public class DCSClass
    {
        [DataMember(Name = "A")]
        [JsonPropertyName("B")]
        public int C { get; set; }
    }

    public class STJClass
    {
        [DataMember(Name = "A")]
        [JsonPropertyName("B")]
        public int C { get; set; }
    }

    private class CustomRequestIdResolver : IJsonTypeInfoResolver
    {
        /// <inheritdoc/>
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            type == typeof(RequestId) ? JsonMetadataServices.CreateValueInfo<RequestId>(options, new OffsetRequestIdConverter()) : null;
    }

    private class OffsetRequestIdConverter : JsonConverter<RequestId>
    {
        /// <inheritdoc/>
        public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64() - 1);

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((value.Number ?? throw new JsonException("Expected a numeric request ID.")) + 1);
    }

    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(STJClass))]
    private partial class ApplicationJsonContext : JsonSerializerContext;
}
