using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class ReadBufferingStreamTests
{
    private const int DefaultCapacity = 10;
    private const int defaultUnderlyingStreamLength = DefaultCapacity * 10;
    private static readonly ImmutableArray<byte> underlyingStreamBuffer = Enumerable.Range(1, defaultUnderlyingStreamLength).Select(n => (byte)n).ToImmutableArray();
    private readonly ITestOutputHelper logger;
    private readonly MemoryStream underlyingStream;
    private ReadBufferingStream bufferingStream;

    public ReadBufferingStreamTests(ITestOutputHelper logger)
    {
        this.logger = logger;
        this.underlyingStream = new MemoryStream(underlyingStreamBuffer.ToArray(), writable: false);
        Assert.True(DefaultCapacity < underlyingStreamBuffer.Length);
        this.bufferingStream = new ReadBufferingStream(this.underlyingStream, DefaultCapacity);
    }

    [Fact]
    public void BufferCapacity()
    {
        Assert.Equal(DefaultCapacity, this.bufferingStream.BufferCapacity);
        this.bufferingStream = new ReadBufferingStream(this.underlyingStream, DefaultCapacity * 2);
        Assert.Equal(DefaultCapacity * 2, this.bufferingStream.BufferCapacity);
    }

    [Fact]
    public void IsBufferEmpty_TrueInitially()
    {
        Assert.True(this.bufferingStream.IsBufferEmpty);
    }

    [Fact]
    public void ReadByte_WithEmptyBufferThrows()
    {
        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.ReadByte());
    }

    [Fact]
    public void Read_WithEmptyBufferThrows()
    {
        byte[] buffer = new byte[100];
        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task ReadAsync_WithEmptyBufferThrows()
    {
        byte[] buffer = new byte[100];
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.bufferingStream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
    }

    [Fact]
    public async Task ReadByte_AfterFillBuffer()
    {
        await this.bufferingStream.FillBufferAsync();
        for (int i = 0; i < this.bufferingStream.BufferCapacity; i++)
        {
            int b = this.bufferingStream.ReadByte();
            Assert.Equal(underlyingStreamBuffer[i], (byte)b);
        }

        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.ReadByte());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadByte_MoreThanCapacitySize(int interval)
    {
        for (int i = 0; i < underlyingStreamBuffer.Length; i++)
        {
            if (this.bufferingStream.IsBufferEmpty || (interval != 0 && (i % interval) == 0))
            {
                await this.bufferingStream.FillBufferAsync();
            }

            int b = this.bufferingStream.ReadByte();
            Assert.NotEqual(-1, b);
            Assert.Equal(underlyingStreamBuffer[i], (byte)b);
        }

        await this.bufferingStream.FillBufferAsync();
        Assert.Equal(-1, this.bufferingStream.ReadByte());
    }

    [Fact]
    public async Task ReadByte_ReturnsMinus1AtEndOfStream()
    {
        var underlyingStream = new MemoryStream(new byte[1] { 5 });
        this.bufferingStream = new ReadBufferingStream(underlyingStream, 5);
        await this.bufferingStream.FillBufferAsync();
        Assert.Equal(5, this.bufferingStream.ReadByte());

        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.ReadByte());
        await this.bufferingStream.FillBufferAsync();
        Assert.Equal(-1, this.bufferingStream.ReadByte());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_DisposesStream(bool disposeStream)
    {
        this.bufferingStream = new ReadBufferingStream(this.underlyingStream, 10, disposeStream);
        this.bufferingStream.Dispose();
        if (disposeStream)
        {
            Assert.Throws<ObjectDisposedException>(() => this.underlyingStream.Seek(0, SeekOrigin.Begin));
        }
        else
        {
            this.underlyingStream.Seek(0, SeekOrigin.Begin);
        }
    }
}
