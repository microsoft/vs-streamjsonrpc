#if NETCOREAPP2_0 || NET452 || NET46

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class WebSocketMessageHandlerTests : TestBase
{
    private const int BufferSize = 9; // an odd number so as to split multi-byte encoded characters
    private static readonly IReadOnlyList<Encoding> Encodings = new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.UTF32 };
    private Random random = new Random();
    private MockWebSocket socket;
    private WebSocketMessageHandler handler;

    public WebSocketMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.socket = new MockWebSocket();
        this.handler = new WebSocketMessageHandler(this.socket, BufferSize);
    }

    public static object[][] EncodingTheoryData
    {
        get
        {
            return Encodings.Select(encoding => new object[] { encoding }).ToArray();
        }
    }

    [Fact]
    public void CtorInputValidation()
    {
        Assert.Throws<ArgumentNullException>(() => new WebSocketMessageHandler(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebSocketMessageHandler(new MockWebSocket(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebSocketMessageHandler(new MockWebSocket(), -1));
        new WebSocketMessageHandler(new MockWebSocket(), 1);
        new WebSocketMessageHandler(new MockWebSocket(), 1 * 1024 * 1024);
    }

    [Fact]
    public void WebSocket_Property()
    {
        Assert.Same(this.socket, this.handler.WebSocket);
    }

    [Fact]
    public void Dispose_DoesNotDisposeSocket()
    {
        this.handler.Dispose();
        Assert.Equal(0, this.socket.DisposalCount);
    }

    [Fact]
    public void Dispose_TwiceDoesNotThrow()
    {
        this.handler.Dispose();
        this.handler.Dispose();
    }

    [Fact]
    public async Task ReturnNullOnRemoteClose()
    {
        // Enqueuing an empty buffer has special meaning to our mock socket
        // that tells it to send a close message.
        this.socket.EnqueueRead(new byte[0]);
        string result = await this.handler.ReadAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task ReadMessage_UnderBufferSize(Encoding encoding)
    {
        this.handler.Encoding = encoding;
        string msg = new string('a', GetMaxCharsThatFitInBuffer(encoding, BufferSize - 1));
        byte[] buffer = encoding.GetBytes(msg);
        this.socket.EnqueueRead(buffer);
        string result = await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg, result);
    }

    [Fact]
    public async Task ReadMessage_ExactBufferSize()
    {
        var encoding = Encoding.UTF8;
        this.handler.Encoding = encoding;
        string msg = new string('a', GetMaxCharsThatFitInBuffer(encoding));
        byte[] buffer = encoding.GetBytes(msg);
        this.socket.EnqueueRead(buffer);
        string result = await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg, result);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task ReadMessage_ExceedsBufferSize(Encoding encoding)
    {
        this.handler.Encoding = encoding;
        string msg = new string('a', (int)(BufferSize * 2.5));
        byte[] buffer = encoding.GetBytes(msg);
        this.socket.EnqueueRead(buffer);
        string result = await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg, result);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task WriteMessage_UnderBufferSize(Encoding encoding)
    {
        this.handler.Encoding = encoding;
        string msg = new string('a', GetMaxCharsThatFitInBuffer(encoding) - 1);
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(msg, writtenString);
    }

    [Fact]
    public async Task WriteMessage_ExactBufferSize()
    {
        Encoding encoding = Encoding.UTF8;
        this.handler.Encoding = encoding;
        string msg = new string('a', GetMaxCharsThatFitInBuffer(encoding));
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(msg, writtenString);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task WriteMessage_ExceedsBufferSize(Encoding encoding)
    {
        this.handler.Encoding = encoding;
        string msg = new string('a', (int)(BufferSize * 2.5));
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(msg, writtenString);
    }

    [Fact]
    public async Task WriteMessage_BufferIsSmallerThanOneEncodedChar()
    {
        this.handler = new WebSocketMessageHandler(this.socket, 2);
        this.handler.Encoding = Encoding.UTF32;
        await Assert.ThrowsAsync<ArgumentException>(() => this.handler.WriteAsync("a", this.TimeoutToken));
    }

    private static int GetMaxCharsThatFitInBuffer(Encoding encoding, int bufferSize = BufferSize) => bufferSize / encoding.GetMaxByteCount(1);

    private byte[] GetRandomBuffer(int count)
    {
        byte[] buffer = new byte[count];
        this.random.NextBytes(buffer);
        return buffer;
    }

    private class Message
    {
        internal ArraySegment<byte> Buffer { get; set; }
    }

    private class MockWebSocket : WebSocket
    {
        private Message writingInProgress;

        public override WebSocketCloseStatus? CloseStatus => throw new NotImplementedException();

        public override string CloseStatusDescription => throw new NotImplementedException();

        public override string SubProtocol => throw new NotImplementedException();

        public override WebSocketState State => throw new NotImplementedException();

        /// <summary>
        /// Gets the queue of messages to be returned from the <see cref="ReceiveAsync(ArraySegment{byte}, CancellationToken)"/> method.
        /// </summary>
        internal Queue<Message> ReadQueue { get; } = new Queue<Message>();

        internal Queue<Message> WrittenQueue { get; } = new Queue<Message>();

        internal int DisposalCount { get; private set; }

        public override void Abort() => throw new NotImplementedException();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => TplExtensions.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => throw new NotImplementedException();

        public override void Dispose() => this.DisposalCount++;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> output, CancellationToken cancellationToken)
        {
            var input = this.ReadQueue.Peek();
            int bytesToCopy = Math.Min(input.Buffer.Count, output.Count);
            Buffer.BlockCopy(input.Buffer.Array, input.Buffer.Offset, output.Array, output.Offset, bytesToCopy);
            bool finishedMessage = bytesToCopy == input.Buffer.Count;
            if (finishedMessage)
            {
                this.ReadQueue.Dequeue();
            }
            else
            {
                input.Buffer = new ArraySegment<byte>(input.Buffer.Array, input.Buffer.Offset + bytesToCopy, input.Buffer.Count - bytesToCopy);
            }

            var result = new WebSocketReceiveResult(
                bytesToCopy,
                WebSocketMessageType.Text,
                finishedMessage,
                bytesToCopy == 0 ? (WebSocketCloseStatus?)WebSocketCloseStatus.Empty : null,
                bytesToCopy == 0 ? "empty" : null);
            return Task.FromResult(result);
        }

        public override Task SendAsync(ArraySegment<byte> input, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (this.writingInProgress == null)
            {
                var bufferCopy = new byte[input.Count];
                Buffer.BlockCopy(input.Array, input.Offset, bufferCopy, 0, input.Count);
                this.writingInProgress = new Message { Buffer = new ArraySegment<byte>(bufferCopy) };
            }
            else
            {
                var bufferCopy = this.writingInProgress.Buffer.Array;
                Array.Resize(ref bufferCopy, bufferCopy.Length + input.Count);
                Buffer.BlockCopy(input.Array, input.Offset, bufferCopy, this.writingInProgress.Buffer.Count, input.Count);
                this.writingInProgress.Buffer = new ArraySegment<byte>(bufferCopy);
            }

            if (endOfMessage)
            {
                this.WrittenQueue.Enqueue(this.writingInProgress);
                this.writingInProgress = null;
            }

            return TplExtensions.CompletedTask;
        }

        internal void EnqueueRead(byte[] buffer)
        {
            this.ReadQueue.Enqueue(new Message { Buffer = new ArraySegment<byte>(buffer) });
        }
    }
}

#endif
