using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft;
using Newtonsoft.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class WebSocketMessageHandlerJsonTests : WebSocketMessageHandlerTests
{
    private static readonly IReadOnlyList<Encoding> Encodings = new Encoding[]
    {
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        new UTF32Encoding(bigEndian: false, byteOrderMark: false),
    };

    public WebSocketMessageHandlerJsonTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }

    public static object[][] EncodingTheoryData
    {
        get
        {
            return Encodings.Select(encoding => new object[] { encoding }).ToArray();
        }
    }

    protected JsonMessageFormatter Formatter => (JsonMessageFormatter)this.formatter;

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task ReadMessage_UnderBufferSize(Encoding encoding)
    {
        this.Formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize - 1);
        var buffer = encoding.GetBytes(this.Formatter.Serialize(msg).ToString(Formatting.None));
        this.socket.EnqueueRead(buffer);
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
    }

    [Fact]
    public async Task ReadMessage_ExactBufferSize()
    {
        var encoding = Encoding.UTF8; // use only UTF8 so we can assume each ASCII character is just one byte.
        this.Formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize);
        var buffer = encoding.GetBytes(this.Formatter.Serialize(msg).ToString(Formatting.None));
        Assumes.True(buffer.Length == BufferSize);
        this.socket.EnqueueRead(buffer);
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task ReadMessage_ExceedsBufferSize(Encoding encoding)
    {
        this.Formatter.Encoding = encoding;
        var msg = this.CreateMessage((int)(BufferSize * 2.5));
        var buffer = encoding.GetBytes(this.Formatter.Serialize(msg).ToString(Formatting.None));
        this.socket.EnqueueRead(buffer);
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
    }

    [Fact]
    public async Task WriteMessage_ExactBufferSize()
    {
        Encoding encoding = new UTF8Encoding(false); // Always use UTF8 so we can assume 1 byte = 1 character
        this.Formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize);
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array!, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(this.Formatter.Serialize(msg).ToString(Formatting.None), writtenString);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task WriteMessage_ExceedsBufferSize(Encoding encoding)
    {
        this.Formatter.Encoding = encoding;
        var msg = this.CreateMessage((int)(BufferSize * 2.5));
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array!, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(this.Formatter.Serialize(msg).ToString(Formatting.None), writtenString);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task WriteMessage_UnderBufferSize(Encoding encoding)
    {
        this.Formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize - 1);
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array!, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(this.Formatter.Serialize(msg).ToString(Formatting.None), writtenString);
    }

    [Fact]
    public async Task WriteMessage_BufferIsSmallerThanOneEncodedChar()
    {
        this.handler = new WebSocketMessageHandler(this.socket, new JsonMessageFormatter(), 2);
        this.Formatter.Encoding = Encoding.UTF32;
        await this.handler.WriteAsync(CreateDefaultMessage(), this.TimeoutToken);
    }

    private JsonRpcRequest CreateMessage(int requiredEncodedBytesCount)
    {
        var msg = CreateDefaultMessage();
        string baseline = this.Formatter.Serialize(msg).ToString(Formatting.None);
        if (baseline.Length > requiredEncodedBytesCount)
        {
            throw new ArgumentException("Cannot make a message that small. The min size is " + baseline.Length, nameof(requiredEncodedBytesCount));
        }

        msg.Method += new string('a', (requiredEncodedBytesCount - baseline.Length) / this.Formatter.Encoding.GetByteCount("a"));
        return msg;
    }
}
