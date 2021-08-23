using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public abstract class WebSocketMessageHandlerTests : TestBase
{
    protected const int BufferSize = (3 * 12) + 1; // an odd number so as to split multi-byte encoded characters
    protected Random random = new Random();
    protected MockWebSocket socket;
    protected WebSocketMessageHandler handler;
    protected IJsonRpcMessageFormatter formatter;

    private const int LengthOfStringQuotes = 2; // ""

    public WebSocketMessageHandlerTests(IJsonRpcMessageFormatter formatter, ITestOutputHelper logger)
        : base(logger)
    {
        this.socket = new MockWebSocket();
        this.formatter = formatter;
        this.handler = new WebSocketMessageHandler(this.socket, this.formatter, BufferSize);
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

    [Fact]
    [Trait("TestCategory", "FailsInCloudTest")] // https://github.com/microsoft/vs-streamjsonrpc/issues/608
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
    [Trait("TestCategory", "FailsInCloudTest")] // https://github.com/microsoft/vs-streamjsonrpc/issues/128
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

    [Fact]
    public async Task DecodeArgument()
    {
        var msg = new JsonRpcRequest
        {
            Method = "test",
            ArgumentsList = new List<object?>
            {
                "somearg",
            },
        };
        using var seq = new Sequence<byte>();
        this.formatter.Serialize(seq, msg);
        this.socket.EnqueueRead(seq.AsReadOnlySequence.ToArray());
        JsonRpcRequest? result = (JsonRpcRequest?)await this.handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(msg.Method, result!.Method);
        Assert.True(result.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object? value));
        Assert.Equal(msg.ArgumentsList[0], value);
    }

    protected static JsonRpcRequest CreateDefaultMessage()
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

    private byte[] GetRandomBuffer(int count)
    {
        byte[] buffer = new byte[count];
        this.random.NextBytes(buffer);
        return buffer;
    }

    protected class Message
    {
        internal ArraySegment<byte> Buffer { get; set; }
    }

    protected class MockWebSocket : WebSocket
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

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotImplementedException();

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
            if (this.writingInProgress is null)
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

#pragma warning disable CA1801 // Review unused parameters
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
#pragma warning restore CA1801 // Review unused parameters

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
