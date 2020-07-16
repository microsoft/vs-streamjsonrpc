// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

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

        using (JsonRpc serverRpc = JsonRpc.Attach(serverStream, server))
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
            await clientStream.WriteAsync(badJson, 0, badJson.Length);

            // The server must fire disonnected event because bad json must make it disconnect
            JsonRpcDisconnectedEventArgs args = await disconnectedEventFired.Task.WithCancellation(this.TimeoutToken);

            Assert.Same(serverRpc, disconnectedEventSender);
            Assert.NotNull(args);
            Assert.NotNull(args.Description);
            Assert.Equal(DisconnectedReason.ParseError, args.Reason);
#pragma warning disable CS0612 // Type or member is obsolete
            Assert.Null(args.LastMessage);
#pragma warning restore CS0612 // Type or member is obsolete
            Assert.NotNull(args.Exception);

            // Server must dispose the stream now
            await serverStreamDisconnected.Task.WithCancellation(this.TimeoutToken);
            Assert.True(serverStream.Disposed);
            Assert.False(serverStream.IsEndReached);
        }
    }

    public class Server
    {
    }
}
