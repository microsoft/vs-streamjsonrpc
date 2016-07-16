namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;

    internal class ReadBufferingStream : Stream
    {
        private readonly Stream underlyingStream;
        private readonly bool disposeUnderlyingStream;
        private byte[] buffer;
        private int start;
        private int length;
        private bool endOfStreamEncountered;

        public ReadBufferingStream(Stream underlyingStream, int bufferSize, bool disposeUnderlyingStream = true)
        {
            Requires.NotNull(underlyingStream, nameof(underlyingStream));
            Requires.Range(bufferSize > 0, nameof(bufferSize), Resources.PositiveIntegerRequired);

            this.underlyingStream = underlyingStream;
            this.buffer = new byte[bufferSize];
            this.disposeUnderlyingStream = disposeUnderlyingStream;
        }

        public bool IsBufferEmpty => this.length == 0;

        public int BufferCapacity => buffer.Length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public async Task FillBufferAsync()
        {
            if (this.length < this.buffer.Length && !this.endOfStreamEncountered)
            {
                int fillStart = (this.start + this.length) % this.buffer.Length;
                var fillEnd = this.buffer.Length - fillStart;
                int bytesRead = 0;
                if (fillEnd - fillStart > 0)
                {
                    bytesRead += await this.underlyingStream.ReadAsync(this.buffer, fillStart, fillEnd - fillStart).ConfigureAwait(false);
                }

                this.length += bytesRead;
                if (bytesRead == 0)
                {
                    this.endOfStreamEncountered = true;
                }
            }
        }

        public override int ReadByte()
        {
            if (this.endOfStreamEncountered)
            {
                return -1;
            }

            if (this.length == 0)
            {
                throw new InvalidOperationException(Resources.FillBufferFirst);
            }

            byte result = this.buffer[this.start];
            this.length--;
            this.start = (this.start + 1) % this.buffer.Length;
            return result;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException(Resources.FillBufferFirst);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(Resources.FillBufferFirst);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.disposeUnderlyingStream)
                {
                    this.underlyingStream.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
