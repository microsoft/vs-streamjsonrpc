using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;

namespace StreamJsonRpc
{
    internal class SplitJoinStream : IDisposable
    {
        internal const int BufferSize = 1024;

        private readonly Stream readable;
        private readonly StringBuilder readBuffer;
        private readonly Stream writable;
        private readonly string delimiter;
        private readonly Encoding encoding;
        private readonly Decoder decoder;
        private readonly bool leaveOpen;
        private readonly bool readTrailing;

        public SplitJoinStream(SplitJoinStreamOptions options)
        {
            Requires.NotNull(options, nameof(options));
            Requires.Argument(options.Readable != null || options.Writable != null, nameof(options), Resources.BothReadableWritableAreNull);
            Requires.Argument(options.Readable == null || options.Readable.CanRead, nameof(options), Resources.ReadableCannotRead);
            Requires.Argument(options.Writable == null || options.Writable.CanWrite, nameof(options), Resources.WritableCannotWrite);
            Requires.Argument(options.Delimiter != string.Empty, nameof(options), Resources.EmptyDelimiter);

            this.readable = options.Readable;
            this.writable = options.Writable;
            this.delimiter = options.Delimiter ?? "\0";
            this.encoding = options.Encoding ?? Encoding.UTF8;
            this.decoder = this.encoding.GetDecoder();
            this.leaveOpen = options.LeaveOpen;
            this.readTrailing = options.ReadTrailing;

            if (this.readable != null)
            {
                this.readBuffer = new StringBuilder(BufferSize);
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async Task<string> ReadAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            Verify.Operation(this.readable != null, Resources.ReadableNotSet);

            string result = this.PopStringFromReadBuffer();
            if (result != null)
            {
                return result;
            }

            var byteBuffer = new byte[BufferSize];
            while (string.IsNullOrEmpty(result))
            {
                // We could have used StreamReader, but it doesn't support cancellation.
                // So we have to fall back to using the stream directly and Decoder() for decoding it.
                // Decoder takes care if there are un-decoded leftovers from the previous reads.
                int byteCount = await this.readable.ReadAsync(byteBuffer, 0, byteBuffer.Length, cancellationToken);
                if (byteCount == 0)
                {
                    // End of stream reached
                    result = this.readTrailing && this.readBuffer.Length > 0 ? this.readBuffer.ToString() : null;
                    this.readBuffer.Clear();
                    return result;
                }

                int count = this.decoder.GetCharCount(byteBuffer, 0, byteCount);
                var buffer = new char[count];
                count = this.decoder.GetChars(byteBuffer, 0, byteCount, buffer, 0);

                this.readBuffer.Append(buffer, 0, count);

                int startIndex = Math.Max(0, this.readBuffer.Length - count - this.delimiter.Length + 1);
                result = this.PopStringFromReadBuffer(startIndex);
            }

            return result;
        }

        public async Task WriteAsync(string message, CancellationToken cancellationToken = default(CancellationToken))
        {
            Verify.Operation(this.writable != null, Resources.WritableNotSet);

            var bytes = this.encoding.GetBytes(message + this.delimiter);
            await this.writable.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
       }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!this.leaveOpen)
                {
                    if (this.readable != null)
                    {
                        this.readable.Dispose();
                    }

                    if (this.writable != null && this.writable != this.readable)
                    {
                        this.writable.Dispose();
                    }
                }
            }
        }

        private string PopStringFromReadBuffer(int startIndex = 0)
        {
            int index = startIndex;
            while (index < this.readBuffer.Length - this.delimiter.Length + 1)
            {
                if (this.readBuffer[index] == this.delimiter[0])
                {
                    bool found = true;
                    for (int j = 1; j < this.delimiter.Length; j++)
                    {
                        if (this.readBuffer[index + j] != this.delimiter[j])
                        {
                            found = false;
                            break;
                        }
                    }

                    if (found)
                    {
                        string result = this.readBuffer.ToString(0, index);
                        this.readBuffer.Remove(0, index + this.delimiter.Length);
                        if (index > 0)
                        {
                            return result;
                        }

                        continue;
                    }

                }

                index++;
            }

            return null;
        }
    }
}
