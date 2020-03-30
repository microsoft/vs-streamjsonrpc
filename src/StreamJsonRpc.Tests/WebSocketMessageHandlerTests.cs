using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class WebSocketMessageHandlerTests : TestBase
{
    private const int BufferSize = (3 * 12) + 1; // an odd number so as to split multi-byte encoded characters
    private const int LengthOfStringQuotes = 2; // ""
    private static readonly IReadOnlyList<Encoding> Encodings = new Encoding[]
    {
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        new UTF32Encoding(bigEndian: false, byteOrderMark: false),
    };

    private Random random = new Random();
    private MockWebSocket socket;
    private WebSocketMessageHandler handler;
    private JsonMessageFormatter formatter = new JsonMessageFormatter();

    public WebSocketMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.socket = new MockWebSocket();
        this.handler = new WebSocketMessageHandler(this.socket, this.formatter, BufferSize);
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
        Assert.Throws<ArgumentNullException>(() => new WebSocketMessageHandler(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebSocketMessageHandler(new MockWebSocket(), new JsonMessageFormatter(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebSocketMessageHandler(new MockWebSocket(), new JsonMessageFormatter(), -1));
        new WebSocketMessageHandler(new MockWebSocket(), new JsonMessageFormatter(), 1);
        new WebSocketMessageHandler(new MockWebSocket(), new JsonMessageFormatter(), 1 * 1024 * 1024);
    }

    [Fact]
    public void WebSocket_Property()
    {
        Assert.Same(this.socket, this.handler.WebSocket);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeSocket()
    {
        await this.handler.DisposeAsync();
        Assert.Equal(0, this.socket.DisposalCount);
    }

    [Fact]
    public void Dispose_TwiceDoesNotThrow()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        this.handler.Dispose();
        this.handler.Dispose();
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task DisposeAsync_TwiceDoesNotThrow()
    {
        await this.handler.DisposeAsync();
        await this.handler.DisposeAsync();
    }

    [Fact]
    public async Task ReturnNullOnRemoteClose()
    {
        // Enqueuing an empty buffer has special meaning to our mock socket
        // that tells it to send a close message.
        this.socket.EnqueueRead(new byte[0]);
        var result = await this.handler.ReadAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task ReadMessage_UnderBufferSize(Encoding encoding)
    {
        this.formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize - 1);
        var buffer = encoding.GetBytes(this.formatter.Serialize(msg).ToString(Formatting.None));
        this.socket.EnqueueRead(buffer);
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
    }

    [Fact]
    public async Task ReadMessage_ExactBufferSize()
    {
        var encoding = Encoding.UTF8; // use only UTF8 so we can assume each ASCII character is just one byte.
        this.formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize);
        var buffer = encoding.GetBytes(this.formatter.Serialize(msg).ToString(Formatting.None));
        Assumes.True(buffer.Length == BufferSize);
        this.socket.EnqueueRead(buffer);
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task ReadMessage_ExceedsBufferSize(Encoding encoding)
    {
        this.formatter.Encoding = encoding;
        var msg = this.CreateMessage((int)(BufferSize * 2.5));
        var buffer = encoding.GetBytes(this.formatter.Serialize(msg).ToString(Formatting.None));
        this.socket.EnqueueRead(buffer);
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task WriteMessage_UnderBufferSize(Encoding encoding)
    {
        this.formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize - 1);
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array!, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(this.formatter.Serialize(msg).ToString(Formatting.None), writtenString);
    }

    [Fact]
    public async Task WriteMessage_ExactBufferSize()
    {
        Encoding encoding = new UTF8Encoding(false); // Always use UTF8 so we can assume 1 byte = 1 character
        this.formatter.Encoding = encoding;
        var msg = this.CreateMessage(BufferSize);
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array!, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(this.formatter.Serialize(msg).ToString(Formatting.None), writtenString);
    }

    [Theory]
    [MemberData(nameof(EncodingTheoryData))]
    public async Task WriteMessage_ExceedsBufferSize(Encoding encoding)
    {
        this.formatter.Encoding = encoding;
        var msg = this.CreateMessage((int)(BufferSize * 2.5));
        await this.handler.WriteAsync(msg, this.TimeoutToken);
        var writtenBuffer = this.socket.WrittenQueue.Dequeue();
        string writtenString = encoding.GetString(writtenBuffer.Buffer.Array!, writtenBuffer.Buffer.Offset, writtenBuffer.Buffer.Count);
        Assert.Equal(this.formatter.Serialize(msg).ToString(Formatting.None), writtenString);
    }

    [Fact]
    public async Task WriteMessage_BufferIsSmallerThanOneEncodedChar()
    {
        this.handler = new WebSocketMessageHandler(this.socket, new JsonMessageFormatter(), 2);
        this.formatter.Encoding = Encoding.UTF32;
        await this.handler.WriteAsync(CreateDefaultMessage(), this.TimeoutToken);
    }

    [Fact]
    public async Task AspNetCoreWebSocket_ServerHangUp()
    {
        var (jsonRpc, webSocket) = await this.EstablishWebSocket();
        using (webSocket)
        using (jsonRpc)
        {
            await jsonRpc.NotifyAsync(nameof(EchoServer.Hangup));
            await jsonRpc.Completion.WithCancellation(this.TimeoutToken);
        }
    }

    [Fact]
    public async Task AspNetCoreWebSocket_DisposeRpcThenCloseSocket()
    {
        var (jsonRpc, webSocket) = await this.EstablishWebSocket();
        Assert.Equal("message1", await jsonRpc.InvokeAsync<string>("Echo", "message1"));
        jsonRpc.Dispose();
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client initiated close", this.TimeoutToken);
    }

    [Fact(Skip = "This test demonstrates what NOT to do.")]
    public async Task AspNetCoreWebSocket_CloseSocketThenDisposeRpc()
    {
        var (jsonRpc, webSocket) = await this.EstablishWebSocket();
        Assert.Equal("message1", await jsonRpc.InvokeAsync<string>("Echo", "message1"));

        // Disposing the socket locally, while StreamJsonRpc is receiving it, leads to an ObjectDisposedException being thrown internally to the WebSocket.
        // Don't do it this way. Instead, dispose of the JsonRpc instance first, then close the socket.
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client initiated close", this.TimeoutToken);
        jsonRpc.Dispose();
    }

    private static JsonRpcRequest CreateDefaultMessage()
    {
        return new JsonRpcRequest
        {
            Method = "t",
        };
    }

    private async Task<(JsonRpc JsonRpc, WebSocket WebSocket)> EstablishWebSocket()
    {
        IWebHostBuilder webHostBuilder = WebHost.CreateDefaultBuilder(Array.Empty<string>())
            .UseStartup<AspNetStartup>();
        var testServer = new TestServer(webHostBuilder);
        var testClient = testServer.CreateWebSocketClient();
        var webSocket = await testClient.ConnectAsync(testServer.BaseAddress, this.TimeoutToken);

        var rpc = new JsonRpc(new WebSocketMessageHandler(webSocket));
        rpc.StartListening();
        return (rpc, webSocket);
    }

    private JsonRpcRequest CreateMessage(int requiredEncodedBytesCount)
    {
        var msg = CreateDefaultMessage();
        string baseline = this.formatter.Serialize(msg).ToString(Formatting.None);
        if (baseline.Length > requiredEncodedBytesCount)
        {
            throw new ArgumentException("Cannot make a message that small. The min size is " + baseline.Length, nameof(requiredEncodedBytesCount));
        }

        msg.Method += new string('a', (requiredEncodedBytesCount - baseline.Length) / this.formatter.Encoding.GetByteCount("a"));
        return msg;
    }

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
        private Message? writingInProgress;

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

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => throw new NotImplementedException();

        public override void Dispose() => this.DisposalCount++;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> output, CancellationToken cancellationToken)
        {
            var input = this.ReadQueue.Peek();
            int bytesToCopy = Math.Min(input.Buffer.Count, output.Count);
            Buffer.BlockCopy(input.Buffer.Array!, input.Buffer.Offset, output.Array!, output.Offset, bytesToCopy);
            bool finishedMessage = bytesToCopy == input.Buffer.Count;
            if (finishedMessage)
            {
                this.ReadQueue.Dequeue();
            }
            else
            {
                input.Buffer = new ArraySegment<byte>(input.Buffer.Array!, input.Buffer.Offset + bytesToCopy, input.Buffer.Count - bytesToCopy);
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
                Buffer.BlockCopy(input.Array!, input.Offset, bufferCopy, 0, input.Count);
                this.writingInProgress = new Message { Buffer = new ArraySegment<byte>(bufferCopy) };
            }
            else
            {
                byte[] bufferCopy = this.writingInProgress.Buffer.Array!;
                Array.Resize(ref bufferCopy, bufferCopy.Length + input.Count);
                Buffer.BlockCopy(input.Array!, input.Offset, bufferCopy, this.writingInProgress.Buffer.Count, input.Count);
                this.writingInProgress.Buffer = new ArraySegment<byte>(bufferCopy);
            }

            if (endOfMessage)
            {
                this.WrittenQueue.Enqueue(this.writingInProgress);
                this.writingInProgress = null;
            }

            return Task.CompletedTask;
        }

        internal void EnqueueRead(byte[] buffer)
        {
            this.ReadQueue.Enqueue(new Message { Buffer = new ArraySegment<byte>(buffer) });
        }
    }

    private class AspNetStartup
    {
        public AspNetStartup(IConfiguration configuration)
        {
            this.Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            app.Use(async (context, next) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    using (var rpc = new JsonRpc(new WebSocketMessageHandler(webSocket), new EchoServer(webSocket)))
                    {
                        rpc.StartListening();
                        await rpc.Completion;
                    }
                }

                await next();
            });
        }
    }

    private class EchoServer
    {
        private readonly WebSocket webSocket;

        internal EchoServer(WebSocket webSocket)
        {
            this.webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        }

        public string Echo(string message) => message;

        public async Task Hangup()
        {
            await this.webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client requested server hang up", CancellationToken.None);
        }
    }
}
