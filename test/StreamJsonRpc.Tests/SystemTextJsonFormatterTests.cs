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
    public void CancellationParameterUsesBuiltInMetadata(bool replaceOptions, bool useResolverChain, bool readOnly, bool stringId)
    {
        Assert.Null(ApplicationJsonContext.Default.GetTypeInfo(typeof(RequestId)));
        JsonSerializerOptions options = replaceOptions ? new() : this.Formatter.JsonSerializerOptions;
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

        if (replaceOptions)
        {
            this.Formatter.JsonSerializerOptions = options;
        }

        this.AssertCancellationRoundtrip(stringId ? new RequestId("request") : new RequestId(42), stringId ? "\"request\"" : "42");
    }

    /// <summary>
    /// Verifies resolver configuration remains possible until incoming user data is consumed.
    /// </summary>
    [Fact]
    public void DeserializationUsesBuiltInMetadata()
    {
        byte[] json = """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":42}}"""u8.ToArray();
        JsonRpcRequest request = Assert.IsAssignableFrom<JsonRpcRequest>(this.Formatter.Deserialize(new ReadOnlySequence<byte>(json)));
        this.Formatter.JsonSerializerOptions.TypeInfoResolver = ApplicationJsonContext.Default;

        Assert.True(request.TryGetArgumentByNameOrIndex("id", 0, typeof(RequestId), out object? actual));
        Assert.Equal(new RequestId(42), Assert.IsType<RequestId>(actual));
    }

    /// <summary>
    /// Verifies results and property bags share the fallback without prematurely locking options.
    /// </summary>
    [Fact]
    public void ResultAndTopLevelPropertyUseBuiltInMetadata()
    {
        IJsonRpcMessageFactory factory = this.Formatter;
        JsonRpcResult result = factory.CreateResultMessage();
        result.Result = new RequestId(42);
        Assert.True(result.TrySetTopLevelProperty("extra", new RequestId("value")));
        this.Formatter.JsonSerializerOptions.TypeInfoResolver = ApplicationJsonContext.Default;

        JsonRpcResult deserialized = this.Roundtrip(result);
        Assert.Equal(new RequestId(42), deserialized.GetResult<RequestId>());
        Assert.True(deserialized.TryGetTopLevelProperty("extra", out RequestId extra));
        Assert.Equal(new RequestId("value"), extra);
    }

    /// <summary>
    /// Verifies user converters and resolver contracts take precedence over the built-in fallback.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomRequestIdSerializationTakesPrecedence(bool useResolver)
    {
        this.Formatter.JsonSerializerOptions.TypeInfoResolver = ApplicationJsonContext.Default;
        if (useResolver)
        {
            this.Formatter.JsonSerializerOptions.TypeInfoResolverChain.Add(new CustomRequestIdResolver());
        }
        else
        {
            this.Formatter.JsonSerializerOptions.Converters.Insert(0, new OffsetRequestIdConverter());
        }

        this.AssertCancellationRoundtrip(new RequestId(42), "43");
    }

    /// <summary>
    /// Verifies replacing previously used options resets preparation and keeps the fallback last.
    /// </summary>
    [Fact]
    public void OptionsReplacementPreservesResolverPrecedence()
    {
        this.Formatter.JsonSerializerOptions.TypeInfoResolver = ApplicationJsonContext.Default;
        this.AssertCancellationRoundtrip(new RequestId(42), "42");
        Assert.True(this.Formatter.JsonSerializerOptions.IsReadOnly);

        JsonSerializerOptions replacement = new(this.Formatter.JsonSerializerOptions);
        CustomRequestIdResolver resolver = new();
        replacement.TypeInfoResolverChain.Add(resolver);
        this.Formatter.JsonSerializerOptions = replacement;

        this.AssertCancellationRoundtrip(new RequestId(42), "43");
        this.AssertCancellationRoundtrip(new RequestId(42), "43");
        Assert.Equal(3, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain.Count);
        Assert.Same(resolver, this.Formatter.JsonSerializerOptions.TypeInfoResolverChain[1]);
        Assert.Same(resolver, replacement.TypeInfoResolverChain[2]);
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
        this.Formatter.JsonSerializerOptions.TypeInfoResolver = ApplicationJsonContext.Default;
        IJsonRpcMessageFactory factory = this.Formatter;
        JsonRpcRequest request = factory.CreateRequestMessage();
        request.Method = "test";
        request.Arguments = new[] { new CustomType { Age = 42 } };

        using Sequence<byte> sequence = new();
        JsonException exception = Assert.Throws<JsonException>(() => this.Formatter.Serialize(sequence, request));
        Assert.IsType<NotSupportedException>(exception.InnerException);
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
