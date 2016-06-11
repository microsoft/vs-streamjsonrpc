using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;
using System.Threading;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.Threading;

public class JsonRpcRawStreamTests
{
    private static TimeSpan TestTimeout => Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource timeoutTokenSource = new CancellationTokenSource(TestTimeout);
    private CancellationToken TimeoutToken => this.timeoutTokenSource.Token;

    [Fact]
    public async Task JsonRpcClosesStreamAfterDisconnectedEvent()
    {
        var server = new Server();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();

        var clientStream = streams.Item2;

        // Use wrapped stream to see when the stream is closed and disposed.
        var serverStream = new WrappedStream(streams.Item1);

        // Subscribe to disconnected event on the server stream
        var serverStreamDisconnected = new TaskCompletionSource<object>();
        serverStream.Disconnected += (sender, args) => serverStreamDisconnected.SetResult(null);

        using (JsonRpc serverRpc = JsonRpc.Attach(serverStream, server))
        {
            // Subscribe to disconnected event on json rpc
            var disconnectedEventFired = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();
            object disconnectedEventSender = null;
            serverRpc.Disconnected += delegate (object sender, JsonRpcDisconnectedEventArgs e)
            {
                // The stream must not be disposed when the Disconnected even fires
                Assert.True(serverStream.IsConnected);

                disconnectedEventSender = sender;
                disconnectedEventFired.SetResult(e);
            };

            // Send a bad json to the server
            using (var split = new SplitJoinStream(new SplitJoinStreamOptions { Writable = clientStream, LeaveOpen = true }))
            {
                await split.WriteAsync("{");
            }

            // The server must fire disonnected event because bad json must make it disconnect
            JsonRpcDisconnectedEventArgs args = await disconnectedEventFired.Task.WithCancellation(this.TimeoutToken);

            Assert.Same(serverRpc, disconnectedEventSender);
            Assert.NotNull(args);
            Assert.NotNull(args.Description);
            Assert.Equal(DisconnectedReason.ParseError, args.Reason);
            Assert.Equal("{", args.LastMessage);
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
