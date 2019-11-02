// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal class WrappedStream : Stream
{
    protected readonly Stream stream;

    private bool isConnected;
    private bool isEndReached;
    private bool disposed;
    private EventHandler? disconnectedListeners;

    public WrappedStream(Stream stream)
    {
        this.stream = stream;
    }

    public event EventHandler Disconnected
    {
        add
        {
            if (value != null)
            {
                this.disconnectedListeners += value;
                this.UpdateConnectedState();
            }
        }

        remove
        {
            this.disconnectedListeners -= value;
        }
    }

    public bool Disposed => this.disposed;

    public bool IsEndReached => this.isEndReached;

    public override bool CanRead => this.stream.CanRead;

    public override bool CanSeek => this.stream.CanSeek;

    public override bool CanTimeout => this.stream.CanTimeout;

    public override bool CanWrite => this.stream.CanWrite;

    public override long Length => this.stream.Length;

    public override long Position
    {
        get
        {
            return this.stream.Position;
        }

        set
        {
            this.stream.Position = value;
        }
    }

    public override int ReadTimeout => this.stream.ReadTimeout;

    public override int WriteTimeout => this.stream.WriteTimeout;

    public bool IsConnected
    {
        get
        {
            bool result = this.GetConnected();
            this.SetConnected(result);
            return result;
        }
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        this.UpdateConnectedState();
        return this.stream.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    public override void Flush()
    {
        this.UpdateConnectedState();
        this.stream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        this.UpdateConnectedState();
        return this.stream.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        this.UpdateConnectedState();
        int result = this.stream.Read(buffer, offset, count);
        if (result == 0)
        {
            this.EndReached();
        }

        return result;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        this.UpdateConnectedState();
        int result = await this.stream.ReadAsync(buffer, offset, count, cancellationToken);
        if (result == 0)
        {
            this.EndReached();
        }

        return result;
    }

    public override int ReadByte()
    {
        this.UpdateConnectedState();
        int result = this.stream.ReadByte();
        if (result == -1)
        {
            this.EndReached();
        }

        return result;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        this.UpdateConnectedState();
        return this.stream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        this.UpdateConnectedState();
        this.stream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.UpdateConnectedState();
        this.stream.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        this.UpdateConnectedState();
        await this.stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void WriteByte(byte value)
    {
        this.UpdateConnectedState();
        this.stream.WriteByte(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!this.disposed)
            {
                this.disposed = true;
                this.stream.Dispose();
                this.UpdateConnectedState();
            }
        }

        base.Dispose(disposing);
    }

    protected virtual bool GetConnected()
    {
        return !this.isEndReached && !this.disposed;
    }

    protected void UpdateConnectedState()
    {
        this.SetConnected(this.GetConnected());
    }

    private void EndReached()
    {
        if (!this.isEndReached)
        {
            this.isEndReached = true;
            this.UpdateConnectedState();
        }
    }

    private void SetConnected(bool value)
    {
        if (this.isConnected != value)
        {
            this.isConnected = value;
            if (!value)
            {
                this.disconnectedListeners?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
