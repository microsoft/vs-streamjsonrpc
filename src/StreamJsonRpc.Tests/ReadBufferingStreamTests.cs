// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
    private const int DefaultUnderlyingStreamLength = DefaultCapacity * 10;
    private static readonly ImmutableArray<byte> UnderlyingStreamBuffer = Enumerable.Range(1, DefaultUnderlyingStreamLength).Select(n => (byte)n).ToImmutableArray();
    private readonly ITestOutputHelper logger;
    private readonly MemoryStream underlyingStream;
    private ReadBufferingStream bufferingStream;

    public ReadBufferingStreamTests(ITestOutputHelper logger)
    {
        this.logger = logger;
        this.underlyingStream = new MemoryStream(UnderlyingStreamBuffer.ToArray(), writable: false);
        Assert.True(UnderlyingStreamBuffer.Length > DefaultCapacity);
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
        byte[] buffer = new byte[DefaultCapacity];
        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task ReadAsync_WithEmptyBuffer()
    {
        byte[] buffer = new byte[DefaultCapacity];
        Assert.Equal(buffer.Length, await this.bufferingStream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
    }

    [Fact]
    public async Task ReadByte_AfterFillBuffer()
    {
        await this.bufferingStream.FillBufferAsync();
        for (int i = 0; i < this.bufferingStream.BufferCapacity; i++)
        {
            int b = this.bufferingStream.ReadByte();
            Assert.Equal(UnderlyingStreamBuffer[i], (byte)b);
        }

        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.ReadByte());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadByte_MoreThanCapacitySize(int interval)
    {
        for (int i = 0; i < UnderlyingStreamBuffer.Length; i++)
        {
            if (this.bufferingStream.IsBufferEmpty || (interval != 0 && (i % interval) == 0))
            {
                await this.bufferingStream.FillBufferAsync();
            }

            int b = this.bufferingStream.ReadByte();
            Assert.NotEqual(-1, b);
            Assert.Equal(UnderlyingStreamBuffer[i], (byte)b);
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

        // Do it again, just to make sure it's repeatable.
        await this.bufferingStream.FillBufferAsync();
        Assert.Equal(-1, this.bufferingStream.ReadByte());
    }

    [Fact]
    public async Task Read_Returns0AtEndOfStream()
    {
        var underlyingStream = new MemoryStream(new byte[] { 1, 2, 3 });
        this.bufferingStream = new ReadBufferingStream(underlyingStream, 5);
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[underlyingStream.Length + 1];
        Assert.Equal(3, this.bufferingStream.Read(buffer, 0, buffer.Length));

        Assert.Throws<InvalidOperationException>(() => this.bufferingStream.Read(buffer, 0, buffer.Length));
        await this.bufferingStream.FillBufferAsync();
        Assert.Equal(0, this.bufferingStream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task Read_OneCapacityBlock()
    {
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[DefaultCapacity];
        Assert.Equal(buffer.Length, this.bufferingStream.Read(buffer, 0, buffer.Length));
        Assert.Equal(UnderlyingStreamBuffer.Take(buffer.Length), buffer);
    }

    [Fact]
    public async Task Read_TwoCapacityBlock()
    {
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[DefaultCapacity * 2];
        int readBytes = this.bufferingStream.Read(buffer, 0, buffer.Length);

        // We only expect up to the buffer size to be returned.
        // Otherwise it means the buffering strea had us waiting longer than necessary to get anything
        // since it effectively started waiting on I/O.
        Assert.Equal(DefaultCapacity, readBytes);
        Assert.Equal(UnderlyingStreamBuffer.Take(readBytes), buffer.Take(readBytes));
    }

    [Fact]
    public async Task Read_HalfCapacityThenFull()
    {
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[DefaultCapacity];

        // Read half the buffered data
        int readBytes = this.bufferingStream.Read(buffer, 0, buffer.Length / 2);
        Assert.Equal(buffer.Length / 2, readBytes);
        Assert.Equal(UnderlyingStreamBuffer.Take(readBytes), buffer.Take(readBytes));

        // Now try to read a full buffer capacity's worth.
        // We should only get what remains in the buffer.
        int readBytes2 = this.bufferingStream.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length - readBytes, readBytes2);
        Assert.Equal(UnderlyingStreamBuffer.Skip(readBytes).Take(readBytes2), buffer.Take(readBytes2));
    }

    [Fact]
    public async Task Read_Wraparound()
    {
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[DefaultCapacity];

        // Read half the buffered data
        int readBytes = this.bufferingStream.Read(buffer, 0, buffer.Length / 2);
        Assert.Equal(buffer.Length / 2, readBytes);
        Assert.Equal(UnderlyingStreamBuffer.Take(readBytes), buffer.Take(readBytes));

        await this.bufferingStream.FillBufferAsync();

        // Now try to read a full buffer capacity's worth.
        // We should get a full buffer since we refilled it.
        int readBytes2 = this.bufferingStream.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, readBytes2);
        Assert.Equal(UnderlyingStreamBuffer.Skip(readBytes).Take(readBytes2), buffer.Take(readBytes2));
    }

    [Fact]
    public async Task Read_InvalidAndEdgeCaseArgs()
    {
        await this.bufferingStream.FillBufferAsync();
        Assert.Equal(5, this.bufferingStream.Read(new byte[5], 0, 5));
        Assert.Equal(0, this.bufferingStream.Read(new byte[5], 0, 0));

        Assert.Throws<ArgumentNullException>(() => this.bufferingStream.Read(null, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => this.bufferingStream.Read(new byte[5], 2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => this.bufferingStream.Read(new byte[5], 0, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => this.bufferingStream.Read(new byte[5], -1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => this.bufferingStream.Read(new byte[5], 2, 4));
    }

    [Fact]
    public async Task ReadAsync_Returns0AtEndOfStream()
    {
        var underlyingStream = new MemoryStream(new byte[] { 1, 2, 3 });
        this.bufferingStream = new ReadBufferingStream(underlyingStream, 5);
        var buffer = new byte[underlyingStream.Length + 1];
        Assert.Equal(3, await this.bufferingStream.ReadAsync(buffer, 0, buffer.Length));
        Assert.Equal(0, await this.bufferingStream.ReadAsync(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task ReadAsync_InvalidAndEdgeCaseArgs()
    {
        Assert.Equal(5, await this.bufferingStream.ReadAsync(new byte[5], 0, 5));
        Assert.Equal(0, await this.bufferingStream.ReadAsync(new byte[5], 0, 0));

        await Assert.ThrowsAsync<ArgumentNullException>(() => this.bufferingStream.ReadAsync(null, 0, 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this.bufferingStream.ReadAsync(new byte[5], 2, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this.bufferingStream.ReadAsync(new byte[5], 0, 6));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this.bufferingStream.ReadAsync(new byte[5], -1, 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this.bufferingStream.ReadAsync(new byte[5], 2, 4));
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

    [Fact]
    public void CanProperties()
    {
        Assert.True(this.bufferingStream.CanRead);
        Assert.False(this.bufferingStream.CanWrite);
        Assert.False(this.bufferingStream.CanSeek);
    }

    [Fact]
    public void UnsupportedMethodsThrow()
    {
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.Flush());
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.Length);
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.Position);
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.Position = 0);
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => this.bufferingStream.Write(new byte[2], 0, 1));
    }

    [Fact]
    public async Task Read_AtBufferSizeAfterFullyDrainBuffer()
    {
        // Fill the buffer, then read half of it so that we have the second half filled.
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[this.bufferingStream.BufferCapacity];
        int bytesRead = this.bufferingStream.Read(buffer, 0, buffer.Length / 2);

        // Now fill the buffer, so that it wraps around.
        // Then drain it fully.
        await this.bufferingStream.FillBufferAsync();
        int bytesRead2 = this.bufferingStream.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, bytesRead2);
        Assert.Equal(UnderlyingStreamBuffer.Skip(bytesRead).Take(bytesRead2), buffer.Take(bytesRead2));

        // Now fill the buffer again. Since the buffer is empty, it *should* fully refill
        // so we can get a full buffer back again.
        await this.bufferingStream.FillBufferAsync();
        int bytesRead3 = this.bufferingStream.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, bytesRead3);
        Assert.Equal(UnderlyingStreamBuffer.Skip(bytesRead + bytesRead2).Take(bytesRead3), buffer.Take(bytesRead3));
    }

    [Fact]
    public async Task ReadAsync_RequestLargeSizeWithEmptyBuffer()
    {
        var buffer = new byte[this.bufferingStream.BufferCapacity * 3];
        int bytesRead = await this.bufferingStream.ReadAsync(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, bytesRead);

        Assert.Equal(UnderlyingStreamBuffer.Take(bytesRead), buffer.Take(bytesRead));
    }

    [Fact]
    public async Task ReadAsync_RequestLargeSizeWithPartiallyFilledBuffer()
    {
        await this.bufferingStream.FillBufferAsync();
        var buffer = new byte[this.bufferingStream.BufferCapacity * 3];

        // The first time we read, it should just give us what's in the internal buffer.
        int bytesRead = await this.bufferingStream.ReadAsync(buffer, 0, buffer.Length);
        Assert.Equal(this.bufferingStream.BufferCapacity, bytesRead);
        Assert.Equal(UnderlyingStreamBuffer.Take(bytesRead), buffer.Take(bytesRead));

        // The second time we read, it should give us everything we ask for because
        // it can skip the internal buffer.
        int bytesRead2 = await this.bufferingStream.ReadAsync(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, bytesRead2);
        Assert.Equal(UnderlyingStreamBuffer.Skip(bytesRead).Take(bytesRead2), buffer.Take(bytesRead2));
    }
}
