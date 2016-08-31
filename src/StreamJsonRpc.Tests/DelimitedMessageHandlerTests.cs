using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class DelimitedMessageHandlerTests : TestBase
{
    private readonly MemoryStream sendingStream = new MemoryStream();
    private readonly MemoryStream receivingStream = new MemoryStream();
    private DirectMessageHandler handler;

    public DelimitedMessageHandlerTests(ITestOutputHelper logger) : base(logger)
    {
        this.handler = new DirectMessageHandler(this.sendingStream, this.receivingStream, Encoding.UTF8);
    }

    [Fact]
    public async Task Ctor_AcceptsNullSendingStream()
    {
        var handler = new DirectMessageHandler(null, this.receivingStream, Encoding.UTF8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.WriteAsync("hi", TimeoutToken));
        string expected = "bye";
        handler.MessagesToRead.Enqueue(expected);
        string actual = await handler.ReadAsync(TimeoutToken);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Ctor_AcceptsNullReceivingStream()
    {
        var handler = new DirectMessageHandler(this.sendingStream, null, Encoding.UTF8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ReadAsync(TimeoutToken));
        string expected = "bye";
        await handler.WriteAsync(expected, TimeoutToken);
        string actual = await handler.WrittenMessages.DequeueAsync(TimeoutToken);
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
    public void WriteAsync_ThrowsObjectDisposedException()
    {
        this.handler.Dispose();
        Task result = this.handler.WriteAsync("content", TimeoutToken);
        Assert.Throws<ObjectDisposedException>(() => result.GetAwaiter().GetResult());
    }

    [Fact]
    public void ReadAsync_ThrowsObjectDisposedException()
    {
        this.handler.Dispose();
        Task result = this.handler.ReadAsync(TimeoutToken);
        Assert.Throws<ObjectDisposedException>(() => result.GetAwaiter().GetResult());
    }
}
