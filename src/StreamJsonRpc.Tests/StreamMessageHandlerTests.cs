// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class StreamMessageHandlerTests : TestBase
{
    private readonly MemoryStream sendingStream = new MemoryStream();
    private readonly MemoryStream receivingStream = new MemoryStream();
    private DirectMessageHandler handler;

    public StreamMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.handler = new DirectMessageHandler();
    }

    [Fact]
    public void CanReadAndWrite()
    {
        Assert.True(this.handler.CanRead);
        Assert.True(this.handler.CanWrite);
    }

    [Fact]
    public async Task Ctor_AcceptsNullSendingStream()
    {
        var handler = new MyStreamMessageHandler(null, this.receivingStream, new JsonMessageFormatter());
        Assert.True(handler.CanRead);
        Assert.False(handler.CanWrite);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.WriteAsync(CreateRequestMessage(), this.TimeoutToken).AsTask());
        JsonRpcMessage expected = CreateNotifyMessage();
        handler.MessagesToRead.Enqueue(expected);
        var actual = await handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Ctor_AcceptsNullReceivingStream()
    {
        var handler = new MyStreamMessageHandler(this.sendingStream, null, new JsonMessageFormatter());
        Assert.False(handler.CanRead);
        Assert.True(handler.CanWrite);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ReadAsync(this.TimeoutToken).AsTask());
        JsonRpcMessage expected = CreateNotifyMessage();
        await handler.WriteAsync(expected, this.TimeoutToken);
        var actual = await handler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsDisposed()
    {
        IDisposableObservable observable = this.handler;
        Assert.False(observable.IsDisposed);
        this.handler.Dispose();
        Assert.True(observable.IsDisposed);
    }

    [Fact]
    public void Dispose_StreamsAreDisposed()
    {
        var streams = FullDuplexStream.CreateStreams();
        var handler = new MyStreamMessageHandler(streams.Item1, streams.Item2, new JsonMessageFormatter());
        Assert.False(streams.Item1.IsDisposed);
        Assert.False(streams.Item2.IsDisposed);
        handler.Dispose();
        Assert.True(streams.Item1.IsDisposed);
        Assert.True(streams.Item2.IsDisposed);
    }

    [Fact]
    public void WriteAsync_ThrowsObjectDisposedException()
    {
        this.handler.Dispose();
        ValueTask result = this.handler.WriteAsync(CreateNotifyMessage(), this.TimeoutToken);
        Assert.Throws<ObjectDisposedException>(() => result.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Verifies that when both <see cref="ObjectDisposedException"/> and <see cref="OperationCanceledException"/> are appropriate
    /// when we first invoke the method, the <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    [Fact]
    public void WriteAsync_PreferOperationCanceledException_AtEntry()
    {
        this.handler.Dispose();
        Assert.Throws<OperationCanceledException>(() => this.handler.WriteAsync(CreateNotifyMessage(), PrecanceledToken).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Verifies that <see cref="MessageHandlerBase.ReadAsync(CancellationToken)"/> prefers throwing
    /// <see cref="OperationCanceledException"/> over <see cref="ObjectDisposedException"/> when both conditions
    /// apply while reading (at least when cancellation occurs first).
    /// </summary>
    [Fact]
    public async Task WriteAsync_PreferOperationCanceledException_MidExecution()
    {
        var handler = new DelayedWriter(this.sendingStream, this.receivingStream, new JsonMessageFormatter());

        var cts = new CancellationTokenSource();
        var writeTask = handler.WriteAsync(CreateRequestMessage(), cts.Token);

        cts.Cancel();
        handler.Dispose();

        // Unblock writer. It should not throw anything as it is to emulate not recognizing the
        // CancellationToken before completing its work.
        handler.WriteBlock.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writeTask.AsTask());
    }

    /// <summary>
    /// Tests that WriteCoreAsync calls cannot be called again till
    /// the Task returned from the prior call has completed.
    /// </summary>
    [Fact]
    public async Task WriteAsync_SemaphoreIncludesWriteCoreAsync_Task()
    {
        var handler = new DelayedWriter(this.sendingStream, this.receivingStream, new JsonMessageFormatter());
        var writeTask = handler.WriteAsync(CreateRequestMessage(), CancellationToken.None).AsTask();
        var write2Task = handler.WriteAsync(CreateRequestMessage(), CancellationToken.None).AsTask();

        // Give the library extra time to do the wrong thing asynchronously.
        await Task.Delay(ExpectedTimeout);

        Assert.Equal(1, handler.WriteCoreCallCount);
        handler.WriteBlock.Set();
        await Task.WhenAll(writeTask, write2Task).WithTimeout(UnexpectedTimeout);
        Assert.Equal(2, handler.WriteCoreCallCount);
    }

    [Fact]
    public void ReadAsync_ThrowsObjectDisposedException()
    {
        this.handler.Dispose();
        ValueTask<JsonRpcMessage> result = this.handler.ReadAsync(this.TimeoutToken);
        Assert.Throws<ObjectDisposedException>(() => result.GetAwaiter().GetResult());
        Assert.Throws<OperationCanceledException>(() => this.handler.ReadAsync(PrecanceledToken).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Verifies that when both <see cref="ObjectDisposedException"/> and <see cref="OperationCanceledException"/> are appropriate
    /// when we first invoke the method, the <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    [Fact]
    public void ReadAsync_PreferOperationCanceledException_AtEntry()
    {
        this.handler.Dispose();
        Assert.Throws<OperationCanceledException>(() => this.handler.ReadAsync(PrecanceledToken).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Verifies that <see cref="MessageHandlerBase.ReadAsync(CancellationToken)"/> prefers throwing
    /// <see cref="OperationCanceledException"/> over <see cref="ObjectDisposedException"/> when both conditions
    /// apply while reading (at least when cancellation occurs first).
    /// </summary>
    [Fact]
    public async Task ReadAsync_PreferOperationCanceledException_MidExecution()
    {
        var cts = new CancellationTokenSource();
        var readTask = this.handler.ReadAsync(cts.Token).AsTask();

        cts.Cancel();
        this.handler.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private static JsonRpcMessage CreateNotifyMessage() => new JsonRpcRequest { Method = "test" };

    private static JsonRpcMessage CreateRequestMessage() => new JsonRpcRequest { RequestId = new RequestId(1), Method = "test" };

    private class DelayedWriter : StreamMessageHandler
    {
        internal readonly AsyncManualResetEvent WriteBlock = new AsyncManualResetEvent();

        internal int WriteCoreCallCount;

        public DelayedWriter(Stream sendingStream, Stream receivingStream, IJsonRpcMessageFormatter formatter)
            : base(sendingStream, receivingStream, formatter)
        {
        }

        protected override ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected override ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.WriteCoreCallCount);
            return new ValueTask(this.WriteBlock.WaitAsync());
        }
    }

    private class MyStreamMessageHandler : StreamMessageHandler
    {
        public MyStreamMessageHandler(Stream sendingStream, Stream receivingStream, IJsonRpcMessageFormatter formatter)
            : base(sendingStream, receivingStream, formatter)
        {
        }

        internal AsyncQueue<JsonRpcMessage> MessagesToRead { get; } = new AsyncQueue<JsonRpcMessage>();

        internal AsyncQueue<JsonRpcMessage> WrittenMessages { get; } = new AsyncQueue<JsonRpcMessage>();

        protected override async ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken)
        {
            return await this.MessagesToRead.DequeueAsync(cancellationToken);
        }

        protected override ValueTask WriteCoreAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            this.WrittenMessages.Enqueue(message);
            return default;
        }
    }
}
