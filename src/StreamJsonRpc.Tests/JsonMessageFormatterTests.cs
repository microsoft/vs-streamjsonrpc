// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Text;
using System.Threading.Tasks;
using Nerdbank.Streams;
using Newtonsoft.Json;
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
        Assert.Throws<ArgumentNullException>(() => formatter.ProtocolVersion = null);
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
            JsonSerializer = { DateParseHandling = DateParseHandling.None },
        };

        string jsonRequest = @"{""jsonrpc"":""2.0"",""method"":""asdf"",""params"":[""2019-01-29T03:37:28.4433841Z""]}";
        ReadOnlySequence<byte> jsonSequence = new ReadOnlySequence<byte>(formatter.Encoding.GetBytes(jsonRequest));
        var jsonMessage = (JsonRpcRequest)formatter.Deserialize(jsonSequence);
        Assert.True(jsonMessage.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object value));
        Assert.IsType<string>(value);
        Assert.Equal("2019-01-29T03:37:28.4433841Z", value);
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

    private static long MeasureLength(JsonRpcRequest msg, JsonMessageFormatter formatter)
    {
        var builder = new Sequence<byte>();
        formatter.Serialize(builder, msg);
        var length = builder.AsReadOnlySequence.Length;
        var readMsg = (JsonRpcRequest)formatter.Deserialize(builder.AsReadOnlySequence);
        Assert.Equal(msg.Method, readMsg.Method);

        return length;
    }
}
