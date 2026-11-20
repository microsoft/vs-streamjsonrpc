// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.VisualStudio.Threading;

public class JsonRpcRawStreamTests : TestBase
{
    public JsonRpcRawStreamTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public async Task JsonRpcClosesStreamAfterDisconnectedEvent()
    {
        var server = new Server();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();

        var clientStream = streams.Item2;

        // Use wrapped stream to see when the stream is closed and disposed.
        var serverStream = new WrappedStream(streams.Item1);

        // Subscribe to disconnected event on the server stream
        var serverStreamDisconnected = new TaskCompletionSource<object?>();
        serverStream.Disconnected += (sender, args) => serverStreamDisconnected.SetResult(null);

        using (JsonRpc serverRpc = JsonRpc.Attach(new HeaderDelimitedMessageHandler(serverStream, CreateAnyJsonFormatter()), server))
        {
            // Subscribe to disconnected event on json rpc
            var disconnectedEventFired = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();
            object? disconnectedEventSender = null;
            serverRpc.Disconnected += (object? sender, JsonRpcDisconnectedEventArgs e) =>
            {
                // The stream must not be disposed when the Disconnected even fires
                Assert.True(serverStream.IsConnected);

                disconnectedEventSender = sender;
                disconnectedEventFired.SetResult(e);
            };

            // Send a bad json to the server
            byte[] badJson = Encoding.UTF8.GetBytes("Content-Length: 1\r\n\r\n{");
            await clientStream.WriteAsync(badJson, 0, badJson.Length, TestContext.Current.CancellationToken);

            // The server must fire disonnected event because bad json must make it disconnect
            JsonRpcDisconnectedEventArgs args = await disconnectedEventFired.Task.WithCancellation(this.TimeoutToken);

            Assert.Same(serverRpc, disconnectedEventSender);
            Assert.NotNull(args);
            Assert.NotNull(args.Description);
            Assert.Equal(DisconnectedReason.ParseError, args.Reason);
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Null(args.LastMessage);
#pragma warning restore CS0618 // Type or member is obsolete
            Assert.NotNull(args.Exception);

            // Server must dispose the stream now
            await serverStreamDisconnected.Task.WithCancellation(this.TimeoutToken);
            Assert.True(serverStream.Disposed);
            Assert.False(serverStream.IsEndReached);
        }
    }

    [Theory]
    [InlineData(0, DisconnectedReason.ParseError)]
    [InlineData(1, DisconnectedReason.ParseError)]
    [InlineData(2, DisconnectedReason.ParseError)]
    [InlineData(3, DisconnectedReason.StreamError)]
    [InlineData(4, DisconnectedReason.StreamError)]
    public async Task ReadExceptionClassification(int exceptionKind, DisconnectedReason expectedReason)
    {
        Exception exception = exceptionKind switch
        {
            0 => new Newtonsoft.Json.JsonSerializationException("bad JSON"),
            1 => new Newtonsoft.Json.JsonReaderException("bad JSON"),
            2 => new DerivedSystemTextJsonException(),
            3 => new JsonException(),
            4 => new IOException("transport error"),
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionKind)),
        };
        using var rpc = new JsonRpc(new FaultingMessageHandler(exception));
        var disconnected = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();
        rpc.Disconnected += (_, args) => disconnected.SetResult(args);
        rpc.StartListening();

        JsonRpcDisconnectedEventArgs result = await disconnected.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Same(exception, result.Exception);
    }

    public class Server
    {
    }

    private class DerivedSystemTextJsonException : System.Text.Json.JsonException
    {
    }

    private class JsonException : Exception
    {
    }

    private class FaultingMessageHandler(Exception exception) : MessageHandlerBase(CreateAnyFormatter())
    {
        public override bool CanRead => true;

        public override bool CanWrite => true;

        protected override ValueTask<StreamJsonRpc.Protocol.JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken) => throw exception;

        protected override ValueTask WriteCoreAsync(StreamJsonRpc.Protocol.JsonRpcMessage content, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override ValueTask FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
