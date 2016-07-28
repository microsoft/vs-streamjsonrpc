using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;

namespace StreamJsonRpc
{
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
            Requires.Argument(underlyingStream.CanRead, nameof(underlyingStream), Resources.StreamMustBeReadable);
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

        /// <summary>
        /// Gets the length of the first block of data in our buffer (before wraparound).
        /// </summary>
        private int FirstBlockLength => Math.Min(this.length, this.buffer.Length - this.start);

        /// <summary>
        /// Gets the length of the block of data that wrapped around to the front of our buffer.
        /// </summary>
        private int WraparoundBlockLength => (this.start + this.length) % this.buffer.Length;

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public async Task FillBufferAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (this.length < this.buffer.Length)
            {
                int fillStart, fillCount;
                if (this.start + this.length < this.buffer.Length)
                {
                    // The buffer has empty space at the end.
                    fillStart = this.start + this.length;
                    fillCount = this.buffer.Length - fillStart;
                }
                else
                {
                    // The buffer's last byte is allocated, but there is available buffer before the start position.
                    fillCount = this.buffer.Length - this.length;
                    fillStart = this.start - fillCount;
                }

                // Note we do NOT call ReadAsync twice in order to ensure we totally fill our buffer because
                // the semantics of reading streams is that if we call twice, it must return a non-empty array
                // of bytes twice (requiring at least two bytes in total). This could lead us to blocking for
                // an unknown length of time for the second byte when our caller only needed one more byte.
                // Instead, we just fill up whatever section of the array we can (even if it's just one byte)
                // and our caller can ask for more if they want it.
                // As an alterative, we *could* create a temporary buffer just large enough for all the bytes
                // we need to fill our wraparound buffer and then manually copy bytes around.
                int bytesRead = await this.underlyingStream.ReadAsync(this.buffer, fillStart, fillCount, cancellationToken).ConfigureAwait(false);
                this.length += bytesRead;

                if (bytesRead == 0)
                {
                    this.endOfStreamEncountered = true;
                }
            }
        }

        public override int ReadByte()
        {
            if (this.length == 0)
            {
                if (this.endOfStreamEncountered)
                {
                    return -1;
                }

                throw new InvalidOperationException(Resources.FillBufferFirst);
            }

            byte result = this.buffer[this.start];
            this.ConsumeBuffer(1);
            return result;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateReadArgs(buffer, offset, count);

            if (this.length == 0)
            {
                if (this.endOfStreamEncountered)
                {
                    return 0;
                }

                throw new InvalidOperationException(Resources.FillBufferFirst);
            }

            count = Math.Min(count, this.length);
            int firstBlockLength = Math.Min(this.FirstBlockLength, count);
            if (firstBlockLength > 0)
            {
                Array.Copy(this.buffer, this.start, buffer, offset, firstBlockLength);
                offset += firstBlockLength;
                this.ConsumeBuffer(firstBlockLength);
            }

            int secondBlockLength = Math.Min(count - firstBlockLength, this.WraparoundBlockLength);
            if (secondBlockLength > 0)
            {
                Array.Copy(this.buffer, 0, buffer, offset, secondBlockLength);
                this.ConsumeBuffer(secondBlockLength);
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateReadArgs(buffer, offset, count);

            if (this.length == 0)
            {
                return this.underlyingStream.ReadAsync(buffer, offset, count, cancellationToken);
            }

            return Task.FromResult(this.Read(buffer, offset, count));
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

        private static void ValidateReadArgs(byte[] buffer, int offset, int count)
        {
            Requires.NotNull(buffer, nameof(buffer));

            // We use if's instead of Requires.Range to avoid loading localized resources
            // except in error conditions for better perf.
            if (offset < 0)
            {
                Requires.FailRange(nameof(offset), Resources.NonNegativeIntegerRequired);
            }

            if (count < 0)
            {
                Requires.FailRange(nameof(count), Resources.NonNegativeIntegerRequired);
            }

            if (offset + count > buffer.Length)
            {
                Requires.FailRange(nameof(count), Resources.SumOfTwoParametersExceedsArrayLength);
            }
        }

        private void ConsumeBuffer(int count)
        {
            this.start = (this.start + count) % this.buffer.Length;
            this.length -= count;

            if (this.length == 0)
            {
                this.start = 0;
            }
        }
    }
}
