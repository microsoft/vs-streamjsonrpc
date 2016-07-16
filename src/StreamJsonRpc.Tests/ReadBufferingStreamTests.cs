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
    private const int randomDataLength = 10 * 1024;
    private static readonly ImmutableArray<byte> randomDataBuffer;
    private readonly ITestOutputHelper logger;
    private readonly MemoryStream randomDataStream;
    private ReadBufferingStream bufferingStream;

    static ReadBufferingStreamTests()
    {
        var r = new Random();
        byte[] buffer = new byte[randomDataLength];
        r.NextBytes(buffer);
        randomDataBuffer = buffer.ToImmutableArray();
    }

    public ReadBufferingStreamTests(ITestOutputHelper logger)
    {
        this.logger = logger;
        this.randomDataStream = new MemoryStream(randomDataBuffer.ToArray(), writable: false);
        const int capacity = 10;
        Assert.True(capacity < randomDataBuffer.Length);
        this.bufferingStream = new ReadBufferingStream(this.randomDataStream, capacity);
    }

    [Fact]
    public void BufferCapacity()
    {
        this.bufferingStream = new ReadBufferingStream(this.randomDataStream, 5);
        Assert.Equal(5, this.bufferingStream.BufferCapacity);
        this.bufferingStream = new ReadBufferingStream(this.randomDataStream, 15);
        Assert.Equal(15, this.bufferingStream.BufferCapacity);
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
            Assert.Equal(randomDataBuffer[i], (byte)b);
        }

        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.ReadByte());
    }

    [Fact]
    public async Task ReadByte_MoreThanCapacitySize_FillWhenEmpty()
    {
        for (int i = 0; i < this.bufferingStream.BufferCapacity * 3; i++)
        {
            if (this.bufferingStream.IsBufferEmpty)
            {
                await this.bufferingStream.FillBufferAsync();
            }

            int b = this.bufferingStream.ReadByte();
            Assert.Equal(randomDataBuffer[i], (byte)b);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadByte_MoreThanCapacitySize_FillMoreFrequently(int interval)
    {
        for (int i = 0; i < this.bufferingStream.BufferCapacity * 3; i++)
        {
            if (this.bufferingStream.IsBufferEmpty || (i % interval) == 0)
            {
                await this.bufferingStream.FillBufferAsync();
            }

            int b = this.bufferingStream.ReadByte();
            Assert.Equal(randomDataBuffer[i], (byte)b);
        }
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
        this.bufferingStream = new ReadBufferingStream(this.randomDataStream, 10, disposeStream);
        this.bufferingStream.Dispose();
        if (disposeStream)
        {
            Assert.Throws<ObjectDisposedException>(() => this.randomDataStream.Seek(0, SeekOrigin.Begin));
        }
        else
        {
            this.randomDataStream.Seek(0, SeekOrigin.Begin);
        }
    }
}
