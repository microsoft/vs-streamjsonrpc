// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public abstract class DuplexPipeMarshalingTests : TestBase, IAsyncLifetime
{
    protected readonly Server server = new Server();
    protected JsonRpc serverRpc;
    protected IJsonRpcMessageFormatter serverMessageFormatter;
    protected MultiplexingStream serverMx;

    protected JsonRpc clientRpc;
    protected IJsonRpcMessageFormatter clientMessageFormatter;
    protected MultiplexingStream clientMx;

    private const string ExpectedFileName = "somefile.jpg";
    private static readonly byte[] MemoryBuffer = Enumerable.Range(1, 50).Select(i => (byte)i).ToArray();

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
    public DuplexPipeMarshalingTests(ITestOutputHelper logger)
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        : base(logger)
    {
    }

    public async Task InitializeAsync()
    {
        Tuple<Nerdbank.FullDuplexStream, Nerdbank.FullDuplexStream> streams = Nerdbank.FullDuplexStream.CreateStreams();

        TraceSource mxServerTraceSource = new TraceSource("MX Server", SourceLevels.Information);
        TraceSource mxClientTraceSource = new TraceSource("MX Client", SourceLevels.Information);

        mxServerTraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        mxClientTraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        MultiplexingStream[] mxStreams = await Task.WhenAll(
            MultiplexingStream.CreateAsync(
                streams.Item1,
                new MultiplexingStream.Options
                {
                    TraceSource = mxServerTraceSource,
                    DefaultChannelTraceSourceFactory = (id, name) => new TraceSource("MX Server channel " + id, SourceLevels.Verbose) { Listeners = { new XunitTraceListener(this.Logger) } },
                },
                this.TimeoutToken),
            MultiplexingStream.CreateAsync(
                streams.Item2,
                new MultiplexingStream.Options
                {
                    TraceSource = mxClientTraceSource,
                    DefaultChannelTraceSourceFactory = (id, name) => new TraceSource("MX Client channel " + id, SourceLevels.Verbose) { Listeners = { new XunitTraceListener(this.Logger) } },
                },
                this.TimeoutToken));
        this.serverMx = mxStreams[0];
        this.clientMx = mxStreams[1];

        MultiplexingStream.Channel[] rpcStreams = await Task.WhenAll(
            this.serverMx.AcceptChannelAsync(string.Empty, this.TimeoutToken),
            this.clientMx.OfferChannelAsync(string.Empty, this.TimeoutToken));
        MultiplexingStream.Channel rpcServerStream = rpcStreams[0];
        MultiplexingStream.Channel rpcClientStream = rpcStreams[1];

        this.InitializeFormattersAndHandlers();

        var serverHandler = new LengthHeaderMessageHandler(rpcServerStream, this.serverMessageFormatter);
        var clientHandler = new LengthHeaderMessageHandler(rpcClientStream, this.clientMessageFormatter);

        this.serverRpc = new JsonRpc(serverHandler, this.server);
        this.clientRpc = new JsonRpc(clientHandler);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Information);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Information);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SendPipeWithoutMultiplexingStream()
    {
        (Stream, Stream) streamPair = FullDuplexStream.CreatePair();
        var clientRpc = JsonRpc.Attach(streamPair.Item1);

        (IDuplexPipe, IDuplexPipe) somePipe = FullDuplexStream.CreatePipePair();
        await Assert.ThrowsAsync<NotSupportedException>(() => clientRpc.InvokeWithCancellationAsync(nameof(Server.TwoWayPipeAsArg), new[] { somePipe.Item2 }, this.TimeoutToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientCanSendReadOnlyPipeToServer(bool orderedArguments)
    {
        (IDuplexPipe, IDuplexPipe) pipes = FullDuplexStream.CreatePipePair();
        pipes.Item1.Input.Complete(); // Indicate that this is only for the server to read -- not write to us.
        await pipes.Item1.Output.WriteAsync(MemoryBuffer, this.TimeoutToken);
        pipes.Item1.Output.Complete();

        int bytesReceived;
        if (orderedArguments)
        {
            bytesReceived = await this.clientRpc.InvokeWithCancellationAsync<int>(
                nameof(Server.AcceptReadablePipe),
                new object[] { ExpectedFileName, pipes.Item2 },
                this.TimeoutToken);
        }
        else
        {
            bytesReceived = await this.clientRpc.InvokeWithParameterObjectAsync<int>(
                nameof(Server.AcceptReadablePipe),
                new { fileName = ExpectedFileName, content = pipes.Item2 },
                this.TimeoutToken);
        }

        Assert.Equal(MemoryBuffer.Length, bytesReceived);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientCanSendWriteOnlyPipeToServer(bool orderedArguments)
    {
        (IDuplexPipe, IDuplexPipe) pipes = FullDuplexStream.CreatePipePair();
        pipes.Item1.Output.Complete(); // we won't ever write anything.

        int bytesToReceive = MemoryBuffer.Length - 1;
        if (orderedArguments)
        {
            await this.clientRpc.InvokeWithCancellationAsync(
                nameof(Server.AcceptWritablePipe),
                new object[] { pipes.Item2, bytesToReceive },
                this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithParameterObjectAsync(
                nameof(Server.AcceptWritablePipe),
                new { lengthToWrite = bytesToReceive, content = pipes.Item2 },
                this.TimeoutToken);
        }

        // Read all that the server wanted us to know, and verify it.
        // TODO: update this when we can detect that the server has finished transmission.
        byte[] buffer = new byte[bytesToReceive + 1];
        int receivedBytes = 0;
        while (receivedBytes < bytesToReceive)
        {
            ReadResult readResult = await pipes.Item1.Input.ReadAsync(this.TimeoutToken);
            foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
            {
                segment.CopyTo(buffer.AsMemory(receivedBytes));
                receivedBytes += segment.Length;
            }

            pipes.Item1.Input.AdvanceTo(readResult.Buffer.End);
        }

        Assert.Equal<byte>(MemoryBuffer.Take(bytesToReceive), buffer.Take(bytesToReceive));
    }

    [Fact]
    public async Task ClientCanSendPipeReaderToServer()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(MemoryBuffer, this.TimeoutToken);
        pipe.Writer.Complete();

        int bytesReceived = await this.clientRpc.InvokeWithCancellationAsync<int>(
            nameof(Server.AcceptPipeReader),
            new object[] { ExpectedFileName, pipe.Reader },
            this.TimeoutToken);

        Assert.Equal(MemoryBuffer.Length, bytesReceived);
    }

    [Fact]
    public async Task ClientCanSendPipeWriterToServer()
    {
        var pipe = new Pipe();

        int bytesToReceive = MemoryBuffer.Length - 1;
        await this.clientRpc.InvokeWithCancellationAsync(
            nameof(Server.AcceptPipeWriter),
            new object[] { pipe.Writer, bytesToReceive },
            this.TimeoutToken);

        // Read all that the server wanted us to know, and verify it.
        // TODO: update this when we can detect that the server has finished transmission.
        byte[] buffer = new byte[bytesToReceive + 1];
        int receivedBytes = 0;
        while (receivedBytes < bytesToReceive)
        {
            ReadResult readResult = await pipe.Reader.ReadAsync(this.TimeoutToken);
            foreach (ReadOnlyMemory<byte> segment in readResult.Buffer)
            {
                segment.CopyTo(buffer.AsMemory(receivedBytes));
                receivedBytes += segment.Length;
            }

            pipe.Reader.AdvanceTo(readResult.Buffer.End);
        }

        Assert.Equal<byte>(MemoryBuffer.Take(bytesToReceive), buffer.Take(bytesToReceive));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientCanSendReadOnlyStreamToServer(bool orderedArguments)
    {
        var ms = new MemoryStream(MemoryBuffer);
        var readOnlyStream = new OneWayStreamWrapper(ms, canRead: true);

        int bytesReceived;
        if (orderedArguments)
        {
            bytesReceived = await this.clientRpc.InvokeWithCancellationAsync<int>(
                nameof(Server.AcceptReadableStream),
                new object[] { ExpectedFileName, readOnlyStream },
                this.TimeoutToken);
        }
        else
        {
            bytesReceived = await this.clientRpc.InvokeWithParameterObjectAsync<int>(
                nameof(Server.AcceptReadableStream),
                new { fileName = ExpectedFileName, content = readOnlyStream },
                this.TimeoutToken);
        }

        Assert.Equal(MemoryBuffer.Length, bytesReceived);

        // Assert that the client-side stream is closed, since the server closed their side.
        await this.AssertStreamClosesAsync(ms);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientCanSendWriteOnlyStreamToServer(bool orderedArguments)
    {
        (Stream, Stream) duplexStream = FullDuplexStream.CreatePair();
        var writeOnlyStream = new OneWayStreamWrapper(duplexStream.Item2, canWrite: true);

        int bytesToReceive = MemoryBuffer.Length - 1;
        if (orderedArguments)
        {
            await this.clientRpc.InvokeWithCancellationAsync(
                nameof(Server.AcceptWritableStream),
                new object[] { writeOnlyStream, bytesToReceive },
                this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithParameterObjectAsync(
                nameof(Server.AcceptWritableStream),
                new { lengthToWrite = bytesToReceive, content = writeOnlyStream },
                this.TimeoutToken);
        }

        // Read all that the server wanted us to know, and verify it.
        byte[] buffer = new byte[bytesToReceive + 1];
        int receivedBytes = 0;
        while (receivedBytes < bytesToReceive)
        {
            int count = await duplexStream.Item1.ReadAsync(buffer, receivedBytes, buffer.Length - receivedBytes);
            receivedBytes += count;
        }

        Assert.Equal(MemoryBuffer.Take(bytesToReceive), buffer.Take(bytesToReceive));

        // Assert that the client-side stream is closed, since the server closed their side.
        await this.AssertStreamClosesAsync(duplexStream.Item2);
    }

    [Fact(Skip = "This would require JsonRpc to hard-code understanding of formatter tricks, or more design work.")]
    public void ServerMethodThatReturnsIDuplexPipeIsRejected()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        Assert.Throws<NotSupportedException>(() => this.serverRpc.AddLocalRpcTarget(new ServerWithIDuplexPipeReturningMethod()));
    }

    [Fact]
    public async Task ServerMethodThatReturnsPipeAsObjectFailsOnReturn()
    {
        await Assert.ThrowsAnyAsync<RemoteRpcException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ReturnPipeAsObject), new object[0], this.TimeoutToken));
    }

    /// <summary>
    /// Verify that the server recoups resources by closing channels directly without expecting the client to do it.
    /// </summary>
    [Fact]
    public async Task ServerHasNoLeakWhenRequestIsRejected()
    {
        Task<MultiplexingStream.Channel> clientRpcChannelTask = this.clientMx.OfferChannelAsync("custom", this.TimeoutToken);
        Task<MultiplexingStream.Channel> serverRpcChannelTask = this.serverMx.AcceptChannelAsync("custom", this.TimeoutToken);
        MultiplexingStream.Channel clientRpcChannel = await clientRpcChannelTask;
        MultiplexingStream.Channel serverRpcChannel = await serverRpcChannelTask;

        this.serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverRpcChannel, new JsonMessageFormatter { MultiplexingStream = this.serverMx }), new ServerWithOverloads());
        this.serverRpc.StartListening();

        // Send a message, advertising a channel.
        // We *deliberately* avoid using the JsonRpc on the client side and send arguments that will cause no good match on the server
        // so as to exercise and verify the path where the server sends an error back to the client, after trying to open the channel, but without invoking the server method,
        // and without the client having a chance to close the channel before we can verify that the server did.
        MultiplexingStream.Channel oobChannel = this.clientMx.CreateChannel();
        var clientHandler = new HeaderDelimitedMessageHandler(clientRpcChannel, new JsonMessageFormatter());
        await clientHandler.WriteAsync(
            new StreamJsonRpc.Protocol.JsonRpcRequest
            {
                RequestId = new RequestId(1),
                Method = nameof(ServerWithOverloads.OverloadedMethod),
                ArgumentsList = new object[] { false, oobChannel.Id, new object() },
            },
            this.TimeoutToken);
        await clientRpcChannel.Output.FlushAsync(this.TimeoutToken);

        // Wait for the client to receive the error message back, but don't let any of our client code handle it.
        ReadResult readResult = await clientRpcChannel.Input.ReadAsync(this.TimeoutToken);
        clientRpcChannel.Input.AdvanceTo(readResult.Buffer.Start);

        // The test intends to exercise that the server actually accepts (or perhaps rejects) the channel.
        await oobChannel.Acceptance.WithCancellation(this.TimeoutToken);

        // Assuming it has accepted it, we want to verify that the server closes the channel.
        await oobChannel.Completion.WithCancellation(this.TimeoutToken);
    }

    [Theory]
    [CombinatorialData]
    public async Task ClientCanSendTwoWayPipeToServer(bool serverUsesStream)
    {
        (IDuplexPipe, IDuplexPipe) pipePair = FullDuplexStream.CreatePipePair();
        Task twoWayCom = TwoWayTalkAsync(pipePair.Item1, writeOnOdd: true, this.TimeoutToken);
        await this.clientRpc.InvokeWithCancellationAsync(serverUsesStream ? nameof(Server.TwoWayStreamAsArg) : nameof(Server.TwoWayPipeAsArg), new object[] { false, pipePair.Item2 }, this.TimeoutToken);
        await twoWayCom.WithCancellation(this.TimeoutToken); // rethrow any exceptions.

        // Confirm that we can see the server is no longer writing.
        ReadResult readResult = await pipePair.Item1.Input.ReadAsync(this.TimeoutToken);
        Assert.True(readResult.IsCompleted);
        Assert.Equal(0, readResult.Buffer.Length);

        pipePair.Item1.Output.Complete();
        pipePair.Item1.Input.Complete();
    }

    [Theory]
    [CombinatorialData]
    public async Task ClientCanSendTwoWayStreamToServer(bool serverUsesStream)
    {
        (Stream, Stream) streamPair = FullDuplexStream.CreatePair();
        Task twoWayCom = TwoWayTalkAsync(streamPair.Item1, writeOnOdd: true, this.TimeoutToken);
        await this.clientRpc.InvokeWithCancellationAsync(serverUsesStream ? nameof(Server.TwoWayStreamAsArg) : nameof(Server.TwoWayPipeAsArg), new object[] { false, streamPair.Item2 }, this.TimeoutToken);
        await twoWayCom.WithCancellation(this.TimeoutToken); // rethrow any exceptions.

        streamPair.Item1.Dispose();
    }

    [Fact]
    public async Task PipeRemainsOpenAfterSuccessfulServerResult()
    {
        (IDuplexPipe, IDuplexPipe) pipePair = FullDuplexStream.CreatePipePair();
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.AcceptPipeAndChatLater), new object[] { false, pipePair.Item2 }, this.TimeoutToken);

        await WhenAllSucceedOrAnyFault(TwoWayTalkAsync(pipePair.Item1, writeOnOdd: true, this.TimeoutToken), this.server.ChatLaterTask!);
        pipePair.Item1.Output.Complete();

        // Verify that the pipe closes when the server completes writing.
        ReadResult readResult = await pipePair.Item1.Input.ReadAsync(this.TimeoutToken);
        Assert.True(readResult.IsCompleted);
    }

    [Fact]
    public async Task ClientClosesChannelsWhenServerErrorsOut()
    {
        (IDuplexPipe, IDuplexPipe) pipePair = FullDuplexStream.CreatePipePair();
        await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.RejectCall), new object[] { pipePair.Item2 }, this.TimeoutToken));

        // Verify that the pipe is closed.
        ReadResult readResult = await pipePair.Item1.Input.ReadAsync(this.TimeoutToken);
        Assert.True(readResult.IsCompleted);
    }

    [Fact]
    public async Task PipesCloseWhenConnectionCloses()
    {
        (IDuplexPipe, IDuplexPipe) pipePair = FullDuplexStream.CreatePipePair();
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.AcceptPipeAndChatLater), new object[] { false, pipePair.Item2 }, this.TimeoutToken);

        this.clientRpc.Dispose();

        // Verify that having closed the RPC connection, the pipes that are open eventually close up.
        ReadResult readResult;
        do
        {
            readResult = await pipePair.Item1.Input.ReadAsync(this.TimeoutToken);
            pipePair.Item1.Input.AdvanceTo(readResult.Buffer.End);
        }
        while (!readResult.IsCompleted);
    }

    [Fact]
    public async Task ClientSendsMultiplePipes()
    {
        (IDuplexPipe, IDuplexPipe) pipePair1 = FullDuplexStream.CreatePipePair();
        (IDuplexPipe, IDuplexPipe) pipePair2 = FullDuplexStream.CreatePipePair();

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.TwoPipes), new object[] { pipePair1.Item2, pipePair2.Item2 }, this.TimeoutToken);
        pipePair1.Item1.Output.Complete();
        pipePair2.Item1.Output.Complete();

        ReadResult pipe1Read = await pipePair1.Item1.Input.ReadAsync(this.TimeoutToken);
        ReadResult pipe2Read = await pipePair2.Item1.Input.ReadAsync(this.TimeoutToken);

        Assert.Equal(1, pipe1Read.Buffer.First.Span[0]);
        Assert.Equal(2, pipe2Read.Buffer.First.Span[0]);
    }

    [Fact]
    public async Task ClientSendsNullPipe()
    {
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.AcceptNullPipe), new object?[] { null }, this.TimeoutToken);
    }

    /// <summary>
    /// Verifies that streams can't be passed off in a notification.
    /// </summary>
    /// <remarks>
    /// The client needs to know when the server is done with the stream, but the client
    /// can't tell whether the server simply hasn't yet processed the notification or never will.
    /// </remarks>
    [Fact]
    public async Task NotifyWithPipe_IsRejectedAtClient()
    {
        (IDuplexPipe, IDuplexPipe) duplexPipes = FullDuplexStream.CreatePipePair();
        await Assert.ThrowsAsync<NotSupportedException>(() => this.clientRpc.NotifyAsync(nameof(Server.AcceptReadableStream), "fileName", duplexPipes.Item2));
    }

    /// <summary>
    /// Verifies that the server never "accepts" the offer of the channel/stream when it won't be invoking a server method with it,
    /// and that the client then cancels the offer.
    /// </summary>
    [Fact]
    public async Task InvokeWithPipe_ServerMethodDoesNotExist_ChannelOfferCanceled()
    {
        int? channelIdOffered = null;
        this.serverMx.ChannelOffered += (s, e) =>
        {
            channelIdOffered = e.Id;
        };
        (IDuplexPipe, IDuplexPipe) duplexPipes = FullDuplexStream.CreatePipePair();
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync("does not exist", new object[] { duplexPipes.Item2 }, this.TimeoutToken));

        // By this point, the server has processed the server method, and the offer for a stream should have preceded that.
        Assert.True(channelIdOffered.HasValue);

        // In response to the server rejecting the RPC request, the client should have canceled the offer for the channel.
        // It may or may not have already occurred so for test stability, we code our assertion to handle both cases.
        try
        {
            MultiplexingStream.Channel serverChannel = this.serverMx.AcceptChannel(channelIdOffered!.Value);

            // The client had not yet canceled the offer. So wait for the client to close the channel now that we've accepted it.
            await serverChannel.Completion.WithCancellation(this.TimeoutToken);
        }
        catch (InvalidOperationException)
        {
            // The client had already canceled the offer.
        }
    }

    /// <summary>
    /// Verifies that in decoding the arguments, the server doesn't do anything it can't do multiple times
    /// in process of selecting the overload to invoke.
    /// </summary>
    [Fact]
    public async Task ClientSendsPipeWhereServerHasMultipleOverloads()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new ServerWithOverloads());

        (IDuplexPipe, IDuplexPipe) pipePair = FullDuplexStream.CreatePipePair();
        Task twoWayCom = TwoWayTalkAsync(pipePair.Item1, writeOnOdd: true, this.TimeoutToken);
        await this.clientRpc.InvokeWithCancellationAsync(nameof(ServerWithOverloads.OverloadedMethod), new object[] { false, pipePair.Item2, "hi" }, this.TimeoutToken);
        await twoWayCom.WithCancellation(this.TimeoutToken); // rethrow any exceptions.

        pipePair.Item1.Output.Complete();
        pipePair.Item1.Input.Complete();
    }

    [Fact]
    [Trait("TestCategory", "FailsInCloudTest")] // https://github.com/microsoft/vs-streamjsonrpc/issues/427
    public async Task StreamClosesDeterministically()
    {
        Tuple<Nerdbank.FullDuplexStream, Nerdbank.FullDuplexStream> streams = Nerdbank.FullDuplexStream.CreateStreams();
        var monitoredStream = new MonitoringStream(new OneWayStreamWrapper(streams.Item1, canWrite: true));
        var disposedEvent = new AsyncManualResetEvent();
        monitoredStream.Disposed += (s, e) => disposedEvent.Set();

        bool writing = false;
        monitoredStream.WillWrite += (s, e) =>
        {
            Assert.False(writing);
            writing = true;
            this.Logger.WriteLine("Writing {0} bytes.", e.Count);
        };
        monitoredStream.WillWriteByte += (s, e) =>
        {
            Assert.False(writing);
            writing = true;
            this.Logger.WriteLine("Writing 1 byte.");
        };
        monitoredStream.WillWriteMemory += (s, e) =>
        {
            Assert.False(writing);
            writing = true;
            this.Logger.WriteLine("Writing {0} bytes.", e.Length);
        };
        monitoredStream.WillWriteSpan += (s, e) =>
        {
            Assert.False(writing);
            writing = true;
            this.Logger.WriteLine("Writing {0} bytes.", e.Length);
        };
        monitoredStream.DidWrite += (s, e) =>
        {
            Assert.True(writing);
            writing = false;
            this.Logger.WriteLine("Wrote {0} bytes.", e.Count);
        };
        monitoredStream.DidWriteByte += (s, e) =>
        {
            Assert.True(writing);
            writing = false;
            this.Logger.WriteLine("Wrote 1 byte.");
        };
        monitoredStream.DidWriteMemory += (s, e) =>
        {
            Assert.True(writing);
            writing = false;
            this.Logger.WriteLine("Wrote {0} bytes.", e.Length);
        };
        monitoredStream.DidWriteSpan += (s, e) =>
        {
            Assert.True(writing);
            writing = false;
            this.Logger.WriteLine("Wrote {0} bytes.", e.Length);
        };

        try
        {
            await this.clientRpc.InvokeWithCancellationAsync(
                nameof(Server.AcceptWritableStream),
                new object[] { monitoredStream, MemoryBuffer.Length },
                this.TimeoutToken);
            this.Logger.WriteLine("RPC call completed.");
        }
        catch (Exception ex) when (!(ex is RemoteInvocationException))
        {
            // The only failure case where the stream will be closed automatically is if it came in as an error response from the server.
            monitoredStream.Dispose();
            throw;
        }

        await disposedEvent.WaitAsync(this.TimeoutToken);
        this.Logger.WriteLine("Stream disposed.");
    }

    protected abstract void InitializeFormattersAndHandlers();

    private static async Task TwoWayTalkAsync(IDuplexPipe pipe, bool writeOnOdd, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 10; i++)
        {
            bool isOdd = i % 2 == 1;
            byte expectedValue = (byte)(i + 1);
            if (isOdd == writeOnOdd)
            {
                await pipe.Output.WriteAsync(new byte[] { expectedValue }, cancellationToken);
            }
            else
            {
                ReadResult readResult = await pipe.Input.ReadAsync(cancellationToken);
                Assert.Equal(1, readResult.Buffer.Length);
                Assert.Equal(expectedValue, readResult.Buffer.First.Span[0]);
                pipe.Input.AdvanceTo(readResult.Buffer.End);
            }
        }
    }

    private static async Task TwoWayTalkAsync(Stream stream, bool writeOnOdd, CancellationToken cancellationToken)
    {
        var buffer = new byte[10];
        for (int i = 0; i < 10; i++)
        {
            bool isOdd = i % 2 == 1;
            byte expectedValue = (byte)(i + 1);
            if (isOdd == writeOnOdd)
            {
                await stream.WriteAsync(new byte[] { expectedValue }, 0, 1, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            else
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, 2, cancellationToken);
                Assert.Equal(1, bytesRead);
                Assert.Equal(expectedValue, buffer[0]);
            }
        }
    }

    private async Task AssertStreamClosesAsync(Stream stream)
    {
        Requires.NotNull(stream, nameof(stream));

        Func<bool> isDisposed = stream is IDisposableObservable observableStream ? new Func<bool>(() => observableStream.IsDisposed) : new Func<bool>(() => !stream.CanRead && !stream.CanWrite);

        while (!this.TimeoutToken.IsCancellationRequested && !isDisposed())
        {
            await Task.Yield();
        }

        Assert.True(isDisposed());
    }

    protected class ServerWithOverloads
    {
        public void OverloadedMethod(bool foo, IDuplexPipe pipe, int[] values)
        {
            pipe.Output.Complete();
            pipe.Input.Complete();
        }

        // We deliberately put this overload in between two others. We're trying to guarantee that the overload picker doesn't pick this one first.
        public async Task OverloadedMethod(bool writeOnOdd, IDuplexPipe pipe, string message, CancellationToken cancellationToken)
        {
            await TwoWayTalkAsync(pipe, writeOnOdd: writeOnOdd, cancellationToken);
        }

        public void OverloadedMethod(bool foo, int value, string[] values) => Assert.NotNull(values);
    }

    protected class Server
    {
        internal Task? ChatLaterTask { get; private set; }

        public async Task<long> AcceptReadablePipe(string fileName, IDuplexPipe content, CancellationToken cancellationToken)
        {
            Assert.Equal(ExpectedFileName, fileName);
            var ms = new MemoryStream();
            using (Stream contentStream = content.AsStream())
            {
                await contentStream.CopyToAsync(ms, 4096, cancellationToken);
                Assert.Equal<byte>(MemoryBuffer, ms.ToArray());
            }

            return ms.Length;
        }

        public async Task AcceptWritablePipe(IDuplexPipe content, int lengthToWrite, CancellationToken cancellationToken)
        {
            // Assert that the pipe is not readable.
            ReadResult readResult = await content.Input.ReadAsync(cancellationToken);
            Assert.Equal(0, readResult.Buffer.Length);
            Assert.True(readResult.IsCompleted);

            const int ChunkSize = 5;
            int writtenBytes = 0;
            while (writtenBytes < lengthToWrite)
            {
                // Write in small chunks to verify that it needn't be written all at once.
                int bytesToWrite = Math.Min(lengthToWrite - writtenBytes, ChunkSize);
                await content.Output.WriteAsync(MemoryBuffer.AsMemory(writtenBytes, bytesToWrite), cancellationToken);
                await content.Output.FlushAsync(cancellationToken);
                writtenBytes += bytesToWrite;
            }

            content.Output.Complete();
        }

        public async Task<long> AcceptPipeReader(string fileName, PipeReader reader, CancellationToken cancellationToken)
        {
            Assert.Equal(ExpectedFileName, fileName);
            var ms = new MemoryStream();
            using (Stream contentStream = reader.AsStream())
            {
                await contentStream.CopyToAsync(ms, 4096, cancellationToken);
                Assert.Equal<byte>(MemoryBuffer, ms.ToArray());
            }

            return ms.Length;
        }

        public async Task AcceptPipeWriter(PipeWriter writer, int lengthToWrite, CancellationToken cancellationToken)
        {
            const int ChunkSize = 5;
            int writtenBytes = 0;
            while (writtenBytes < lengthToWrite)
            {
                // Write in small chunks to verify that it needn't be written all at once.
                int bytesToWrite = Math.Min(lengthToWrite - writtenBytes, ChunkSize);
                await writer.WriteAsync(MemoryBuffer.AsMemory(writtenBytes, bytesToWrite), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                writtenBytes += bytesToWrite;
            }

            writer.Complete();
        }

        public async Task<long> AcceptReadableStream(string fileName, Stream content, CancellationToken cancellationToken)
        {
            Assert.Equal(ExpectedFileName, fileName);
            var ms = new MemoryStream();
            await content.CopyToAsync(ms, 4096, cancellationToken);
            Assert.Equal<byte>(MemoryBuffer, ms.ToArray());
            content.Dispose();
            return ms.Length;
        }

        public async Task AcceptWritableStream(Stream content, int lengthToWrite, CancellationToken cancellationToken)
        {
            Requires.Range(lengthToWrite <= MemoryBuffer.Length, nameof(lengthToWrite));
            const int ChunkSize = 5;
            int writtenBytes = 0;
            while (writtenBytes < lengthToWrite)
            {
                // Write in small chunks to verify that it needn't be written all at once.
                int bytesToWrite = Math.Min(lengthToWrite - writtenBytes, ChunkSize);
                await content.WriteAsync(MemoryBuffer, writtenBytes, bytesToWrite, cancellationToken);
                await content.FlushAsync(cancellationToken);
                writtenBytes += bytesToWrite;
            }

            content.Dispose();
        }

        public async Task TwoWayPipeAsArg(bool writeOnOdd, IDuplexPipe pipe, CancellationToken cancellationToken)
        {
            await TwoWayTalkAsync(pipe, writeOnOdd: writeOnOdd, cancellationToken);
            pipe.Output.Complete();
            pipe.Input.Complete();
        }

        public async Task TwoWayStreamAsArg(bool writeOnOdd, Stream stream, CancellationToken cancellationToken)
        {
            await TwoWayTalkAsync(stream, writeOnOdd: writeOnOdd, cancellationToken);
            stream.Dispose();
        }

        public async Task TwoPipes(IDuplexPipe pipe1, IDuplexPipe pipe2, CancellationToken cancellationToken)
        {
            await pipe1.Output.WriteAsync(new byte[] { 1 }, cancellationToken);
            pipe1.Output.Complete();
            await pipe2.Output.WriteAsync(new byte[] { 2 }, cancellationToken);
            pipe2.Output.Complete();
        }

        public Task AcceptPipeAndChatLater(bool writeOnOdd, IDuplexPipe pipe, CancellationToken cancellationToken)
        {
            this.ChatLaterTask = Task.Run(() => this.TwoWayPipeAsArg(writeOnOdd, pipe, CancellationToken.None));
            return Task.CompletedTask;
        }

        public void AcceptNullPipe(IDuplexPipe pipe) => Assert.Null(pipe);

        public Task RejectCall(IDuplexPipe pipe) => Task.FromException(new InvalidOperationException("Expected test exception."));

        public object ReturnPipeAsObject() => FullDuplexStream.CreatePipePair().Item1;
    }

    protected class ServerWithIDuplexPipeReturningMethod
    {
        public IDuplexPipe? MethodThatReturnsIDuplexPipe() => null;
    }

    protected class OneWayStreamWrapper : Stream
    {
        private readonly Stream innerStream;
        private readonly bool canRead;
        private readonly bool canWrite;

        internal OneWayStreamWrapper(Stream innerStream, bool canRead = false, bool canWrite = false)
        {
            if (canRead == canWrite)
            {
                throw new ArgumentException("Exactly one operation (read or write) must be true.");
            }

            Requires.Argument(innerStream.CanRead || !canRead, nameof(canRead), "Underlying stream is not readable.");
            Requires.Argument(innerStream.CanWrite || !canWrite, nameof(canWrite), "Underlying stream is not writeable.");

            this.innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            this.canRead = canRead;
            this.canWrite = canWrite;
        }

        public override bool CanRead => this.canRead && this.innerStream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => this.canWrite && this.innerStream.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            if (this.CanWrite)
            {
                this.innerStream.Flush();
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (this.CanRead)
            {
                return this.innerStream.Read(buffer, offset, count);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (this.CanRead)
            {
                return this.innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (this.CanWrite)
            {
                this.innerStream.Write(buffer, offset, count);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (this.CanWrite)
            {
                return this.innerStream.WriteAsync(buffer, offset, count, cancellationToken);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.innerStream.Dispose();
            }
        }
    }
}
