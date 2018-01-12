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
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class DelimitedMessageHandlerTests : TestBase
{
    private readonly MemoryStream sendingStream = new MemoryStream();
    private readonly MemoryStream receivingStream = new MemoryStream();
    private DirectMessageHandler handler;

    public DelimitedMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.handler = new DirectMessageHandler(this.sendingStream, this.receivingStream, Encoding.UTF8);
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
        var handler = new DirectMessageHandler(null, this.receivingStream, Encoding.UTF8);
        Assert.True(handler.CanRead);
        Assert.False(handler.CanWrite);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.WriteAsync("hi", this.TimeoutToken));
        string expected = "bye";
        handler.MessagesToRead.Enqueue(expected);
        string actual = await handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Ctor_AcceptsNullReceivingStream()
    {
        var handler = new DirectMessageHandler(this.sendingStream, null, Encoding.UTF8);
        Assert.False(handler.CanRead);
        Assert.True(handler.CanWrite);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ReadAsync(this.TimeoutToken));
        string expected = "bye";
        await handler.WriteAsync(expected, this.TimeoutToken);
        string actual = await handler.WrittenMessages.DequeueAsync(this.TimeoutToken);
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
        var handler = new DirectMessageHandler(streams.Item1, streams.Item2, Encoding.UTF8);
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
        Task result = this.handler.WriteAsync("content", this.TimeoutToken);
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
        Assert.Throws<OperationCanceledException>(() => this.handler.WriteAsync("content", PrecanceledToken).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Verifies that <see cref="DelimitedMessageHandler.ReadAsync(CancellationToken)"/> prefers throwing
    /// <see cref="OperationCanceledException"/> over <see cref="ObjectDisposedException"/> when both conditions
    /// apply while reading (at least when cancellation occurs first).
    /// </summary>
    [Fact]
    public async Task WriteAsync_PreferOperationCanceledException_MidExecution()
    {
        var handler = new DelayedWriter(this.sendingStream, this.receivingStream, Encoding.UTF8);

        var cts = new CancellationTokenSource();
        var writeTask = handler.WriteAsync("content", cts.Token);

        cts.Cancel();
        handler.Dispose();

        // Unblock writer. It should not throw anything as it is to emulate not recognizing the
        // CancellationToken before completing its work.
        handler.WriteBlock.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writeTask);
    }

    /// <summary>
    /// Tests that WriteCoreAsync calls cannot be called again till
    /// the Task returned from the prior call has completed.
    /// </summary>
    [Fact]
    public async Task WriteAsync_SemaphoreIncludesWriteCoreAsync_Task()
    {
        var handler = new DelayedWriter(this.sendingStream, this.receivingStream, Encoding.UTF8);
        var writeTask = handler.WriteAsync("content", CancellationToken.None);
        var write2Task = handler.WriteAsync("content", CancellationToken.None);

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
        Task result = this.handler.ReadAsync(this.TimeoutToken);
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
    /// Verifies that <see cref="DelimitedMessageHandler.ReadAsync(CancellationToken)"/> prefers throwing
    /// <see cref="OperationCanceledException"/> over <see cref="ObjectDisposedException"/> when both conditions
    /// apply while reading (at least when cancellation occurs first).
    /// </summary>
    [Fact]
    public async Task ReadAsync_PreferOperationCanceledException_MidExecution()
    {
        var cts = new CancellationTokenSource();
        var readTask = this.handler.ReadAsync(cts.Token);

        cts.Cancel();
        this.handler.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    private class DelayedWriter : DelimitedMessageHandler
    {
        internal readonly AsyncManualResetEvent WriteBlock = new AsyncManualResetEvent();

        internal int WriteCoreCallCount;

        public DelayedWriter(Stream sendingStream, Stream receivingStream, Encoding encoding)
            : base(sendingStream, receivingStream, encoding)
        {
        }

        protected override Task<string> ReadCoreAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected override Task WriteCoreAsync(string content, Encoding contentEncoding, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.WriteCoreCallCount);
            return this.WriteBlock.WaitAsync();
        }
    }
}
