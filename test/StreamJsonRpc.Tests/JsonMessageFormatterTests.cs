// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Text;
using System.Threading.Tasks;
using Nerdbank.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonMessageFormatterTests : TestBase
{
    public JsonMessageFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void DefaultEncodingLacksPreamble()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Empty(formatter.Encoding.GetPreamble());
    }

    [Fact]
    public void ProtocolVersion_Default()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Equal(new Version(2, 0), formatter.ProtocolVersion);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public void ProtocolVersion_AcceptedVersions(int major, int minor)
    {
        Version expectedVersion = new Version(major, minor);
        var formatter = new JsonMessageFormatter
        {
            ProtocolVersion = expectedVersion,
        };
        Assert.Equal(expectedVersion, formatter.ProtocolVersion);
    }

    [Fact]
    public void ProtocolVersion_RejectsOtherVersions()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Throws<ArgumentNullException>(() => formatter.ProtocolVersion = null!);
        Assert.Throws<NotSupportedException>(() => formatter.ProtocolVersion = new Version(0, 0));
        Assert.Throws<NotSupportedException>(() => formatter.ProtocolVersion = new Version(1, 1));
        Assert.Throws<NotSupportedException>(() => formatter.ProtocolVersion = new Version(2, 1));
        Assert.Throws<NotSupportedException>(() => formatter.ProtocolVersion = new Version(3, 0));
    }

    [Fact]
    public void EncodingProperty_UsedToFormat()
    {
        var msg = new JsonRpcRequest { Method = "a" };

        var formatter = new JsonMessageFormatter(Encoding.ASCII);
        long asciiLength = MeasureLength(msg, formatter);

        formatter.Encoding = Encoding.UTF32;
        var utf32Length = MeasureLength(msg, formatter);
        Assert.Equal(asciiLength * 4, utf32Length - Encoding.UTF32.GetPreamble().Length);
    }

    [Fact]
    public void EncodingPreambleWrittenOnlyOncePerMessage()
    {
        // Contrive a very long message, designed to exceed any buffer that would be used internally by the formatter.
        // The goal here is to result in multiple write operations in order to coerce a second preamble to be written if there were a bug.
        var msg = new JsonRpcRequest { Method = new string('a', 16 * 1024) };

        var formatter = new JsonMessageFormatter(Encoding.ASCII);
        long asciiLength = MeasureLength(msg, formatter);

        formatter.Encoding = Encoding.UTF32;
        var utf32Length = MeasureLength(msg, formatter);
        Assert.Equal(asciiLength * 4, utf32Length - Encoding.UTF32.GetPreamble().Length);

        // Measure UTF32 again to verify the length doesn't change (and the preamble is thus applied to each message).
        var utf32Length2 = MeasureLength(msg, formatter);
        Assert.Equal(utf32Length, utf32Length2);
    }

    [Fact]
    public void SerializerDefaults()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Equal(ConstructorHandling.AllowNonPublicDefaultConstructor, formatter.JsonSerializer.ConstructorHandling);
        Assert.Equal(NullValueHandling.Ignore, formatter.JsonSerializer.NullValueHandling);
    }

    [Fact]
    public void JTokenParserHonorsSettingsOnSerializer()
    {
        var formatter = new JsonMessageFormatter()
        {
            JsonSerializer = { DateParseHandling = DateParseHandling.DateTime },
        };

        string jsonRequest = @"{""jsonrpc"":""2.0"",""method"":""asdf"",""params"":[""2019-01-29T03:37:28.4433841Z""]}";
        ReadOnlySequence<byte> jsonSequence = new ReadOnlySequence<byte>(formatter.Encoding.GetBytes(jsonRequest));
        var jsonMessage = (JsonRpcRequest)formatter.Deserialize(jsonSequence);
        Assert.True(jsonMessage.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object? value));
        Assert.IsType<string>(value);

        // Verify that Newtonsoft.JSON rewrote the string to some other representation.
        Assert.NotEqual("2019-01-29T03:37:28.4433841Z", value);
    }

    /// <summary>
    /// Verifies that the <see cref="JsonMessageFormatter.MultiplexingStream"/> property
    /// retains values it is set to, and accepts null.
    /// </summary>
    /// <remarks>
    /// The rest of the multiplexing stream tests are defined in <see cref="DuplexPipeMarshalingTests"/>.
    /// </remarks>
    [Fact]
    public async Task MultiplexingStream()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Null(formatter.MultiplexingStream);

        Tuple<Nerdbank.FullDuplexStream, Nerdbank.FullDuplexStream> streams = Nerdbank.FullDuplexStream.CreateStreams();
        MultiplexingStream[] mxStreams = await Task.WhenAll(
            Nerdbank.Streams.MultiplexingStream.CreateAsync(streams.Item1, this.TimeoutToken),
            Nerdbank.Streams.MultiplexingStream.CreateAsync(streams.Item2, this.TimeoutToken));

        formatter.MultiplexingStream = mxStreams[0];
        Assert.Same(mxStreams[0], formatter.MultiplexingStream);

        formatter.MultiplexingStream = null;
        Assert.Null(formatter.MultiplexingStream);
    }

    [Fact]
    public void ServerReturnsErrorWithNullId()
    {
        var formatter = new JsonMessageFormatter();
        JsonRpcMessage? message = formatter.Deserialize(JObject.FromObject(new
        {
            jsonrpc = "2.0",
            error = new
            {
                code = -1,
                message = "Some message",
            },
            id = (object?)null,
        }));
        var error = Assert.IsAssignableFrom<JsonRpcError>(message);
        Assert.True(error.RequestId.IsNull);
    }

    [Fact]
    public void ErrorResponseOmitsNullDataField()
    {
        var formatter = new JsonMessageFormatter();
        JToken jtoken = formatter.Serialize(new JsonRpcError { RequestId = new RequestId(1), Error = new JsonRpcError.ErrorDetail { Code = JsonRpcErrorCode.InternalError, Message = "some error" } });
        this.Logger.WriteLine(jtoken.ToString(Formatting.Indented));
        Assert.Equal((int)JsonRpcErrorCode.InternalError, jtoken["error"]!["code"]);
        Assert.Null(jtoken["error"]!["data"]); // we're testing for an undefined field -- not a field with a null value.
    }

    [Fact]
    public void ErrorResponseIncludesNonNullDataField()
    {
        var formatter = new JsonMessageFormatter();
        JToken jtoken = formatter.Serialize(new JsonRpcError { RequestId = new RequestId(1), Error = new JsonRpcError.ErrorDetail { Code = JsonRpcErrorCode.InternalError, Message = "some error", Data = new { more = "info" } } });
        this.Logger.WriteLine(jtoken.ToString(Formatting.Indented));
        Assert.Equal((int)JsonRpcErrorCode.InternalError, jtoken["error"]!["code"]);
        Assert.Equal("info", jtoken["error"]!["data"]!.Value<string>("more"));
    }

    [Fact]
    public void DeserializingResultWithMissingIdFails()
    {
        var formatter = new JsonMessageFormatter();
        var resultWithNoId = JObject.FromObject(new
        {
            jsonrpc = "2.0",
            result = new
            {
                asdf = "abc",
            },
        });
        var message = Assert.Throws<JsonSerializationException>(() => formatter.Deserialize(resultWithNoId)).InnerException?.Message;
        Assert.Contains("\"id\" property missing.", message);
    }

    [Fact]
    public void DeserializingErrorWithMissingIdFails()
    {
        var formatter = new JsonMessageFormatter();
        var errorWithNoId = JObject.FromObject(new
        {
            jsonrpc = "2.0",
            error = new
            {
                code = -1,
                message = "Some message",
            },
        });
        var message = Assert.Throws<JsonSerializationException>(() => formatter.Deserialize(errorWithNoId)).InnerException?.Message;
        Assert.Contains("\"id\" property missing.", message);
    }

    /// <summary>
    /// Verifies that we 'fix' the Newtonsoft.Json default date parsing behavior so that <see href="https://github.com/JamesNK/Newtonsoft.Json/issues/862">strings aren't arbitrarily changed</see>.
    /// </summary>
    [Fact]
    public void DateParseHandling_Default()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Equal(DateParseHandling.None, formatter.JsonSerializer.DateParseHandling);

        // Verify that the behavior matches the setting.
        string jsonRequest = @"{""jsonrpc"":""2.0"",""method"":""asdf"",""params"":[""2019-01-29T03:37:28.4433841Z""]}";
        ReadOnlySequence<byte> jsonSequence = new ReadOnlySequence<byte>(formatter.Encoding.GetBytes(jsonRequest));
        var jsonMessage = (JsonRpcRequest)formatter.Deserialize(jsonSequence);
        Assert.True(jsonMessage.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object? value));
        Assert.IsType<string>(value);
        Assert.Equal("2019-01-29T03:37:28.4433841Z", value);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerialized()
    {
        var formatter = new JsonMessageFormatter();
        IJsonRpcMessageFactory factory = formatter;
        var jsonRequest = factory.CreateRequestMessage();
        Assert.NotNull(jsonRequest);

        jsonRequest.Method = "test";
        Assert.True(jsonRequest.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(jsonRequest.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        var messageJsonObject = formatter.Serialize(jsonRequest);
        var jsonMessage = (JsonRpcRequest)formatter.Deserialize(messageJsonObject);

        Assert.True(jsonMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(jsonMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedInError()
    {
        var formatter = new JsonMessageFormatter();
        IJsonRpcMessageFactory factory = formatter;
        var jsonError = factory.CreateErrorMessage();
        jsonError.Error = new JsonRpcError.ErrorDetail() { Message = "test" };

        Assert.True(jsonError.TrySetTopLevelProperty("testProperty", "testValue"));

        var messageJsonObject = formatter.Serialize(jsonError);
        var jsonMessage = (JsonRpcError)formatter.Deserialize(messageJsonObject);

        Assert.True(jsonMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedInResult()
    {
        var formatter = new JsonMessageFormatter();
        IJsonRpcMessageFactory factory = formatter;
        var jsonResult = factory.CreateResultMessage();
        Assert.True(jsonResult.TrySetTopLevelProperty("testProperty", "testValue"));
        var messageJsonObject = formatter.Serialize(jsonResult);
        var jsonMessage = (JsonRpcResult)formatter.Deserialize(messageJsonObject);
        Assert.True(jsonMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);
    }

    [Fact]
    public void TopLevelPropertiesWithNullValue()
    {
        var formatter = new JsonMessageFormatter();
        IJsonRpcMessageFactory factory = formatter;
        var jsonRequest = factory.CreateRequestMessage();
        Assert.NotNull(jsonRequest);

        jsonRequest.Method = "test";
        Assert.True(jsonRequest.TrySetTopLevelProperty<string?>("testProperty", null));

        var messageJsonObject = formatter.Serialize(jsonRequest);
        var jsonMessage = (JsonRpcRequest)formatter.Deserialize(messageJsonObject);

        Assert.True(jsonMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void CustomParametersObjectWithJsonConverterProperties()
    {
        const string localPathUri = "file:///c:/foo";
        var formatter = new JsonMessageFormatter();
        JToken token = formatter.Serialize(new JsonRpcRequest { Method = "test", Arguments = new CustomTypeWithJsonConverterProperties { Uri = new Uri(localPathUri) } });
        this.Logger.WriteLine(token.ToString(Formatting.Indented));
        Assert.Equal(CustomConverter.CustomPrefix + localPathUri, token["params"]![nameof(CustomTypeWithJsonConverterProperties.Uri)]!.ToString());

        token = formatter.Serialize(new JsonRpcRequest { Method = "test", Arguments = new CustomTypeWithJsonConverterProperties { } });
        this.Logger.WriteLine(token.ToString(Formatting.Indented));
        Assert.Equal(JTokenType.Null, token["params"]![nameof(CustomTypeWithJsonConverterProperties.Uri)]!.Type);
        Assert.Null(token["params"]![nameof(CustomTypeWithJsonConverterProperties.UriIgnorable)]);
    }

    private static long MeasureLength(JsonRpcRequest msg, JsonMessageFormatter formatter)
    {
        var builder = new Sequence<byte>();
        formatter.Serialize(builder, msg);
        var length = builder.AsReadOnlySequence.Length;
        var readMsg = (JsonRpcRequest)formatter.Deserialize(builder.AsReadOnlySequence);
        Assert.Equal(msg.Method, readMsg.Method);

        return length;
    }

    public class CustomType
    {
        public int Age { get; set; }
    }

    public class CustomTypeWithJsonConverterProperties
    {
        [JsonConverter(typeof(CustomConverter))]
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Uri? Uri { get; set; }

        [JsonConverter(typeof(CustomConverter))]
        public Uri? UriIgnorable { get; set; }
    }

    private class CustomConverter : JsonConverter
    {
        internal const string CustomPrefix = "custom: ";

        public override bool CanConvert(Type objectType) => objectType == typeof(Uri);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            string? value = reader.ReadAsString();
            if (value?.StartsWith(CustomPrefix, StringComparison.Ordinal) is true)
            {
                return new Uri(value.Substring(CustomPrefix.Length));
            }

            throw new InvalidOperationException("Unexpected format.");
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteValue(CustomPrefix + "null");
            }
            else if (value is Uri uri)
            {
                writer.WriteValue(CustomPrefix + uri.AbsoluteUri);
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }
}
