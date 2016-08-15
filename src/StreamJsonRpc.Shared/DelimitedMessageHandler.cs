using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;

namespace StreamJsonRpc
{
    public abstract class DelimitedMessageHandler : IDisposable
    {
        private readonly CancellationTokenSource disposalTokenSource = new CancellationTokenSource();

        private readonly AsyncSemaphore sendingSemaphore = new AsyncSemaphore(1);

        private readonly AsyncSemaphore receivingSemaphore = new AsyncSemaphore(1);

        private Encoding encoding;

        protected DelimitedMessageHandler(Stream sendingStream, Stream receivingStream, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));
            Requires.Argument(sendingStream == null || sendingStream.CanWrite, nameof(sendingStream), Resources.StreamMustBeWriteable);
            Requires.Argument(receivingStream == null || receivingStream.CanRead, nameof(receivingStream), Resources.StreamMustBeReadable);

            this.SendingStream = sendingStream;
            this.ReceivingStream = receivingStream;
            this.encoding = encoding;
        }

        /// <summary>
        /// Gets or sets the encoding to use for transmitted messages.
        /// </summary>
        public Encoding Encoding
        {
            get
            {
                return this.encoding;
            }

            set
            {
                Requires.NotNull(value, nameof(value));
                this.encoding = value;
            }
        }

        protected Stream SendingStream { get; }

        protected Stream ReceivingStream { get; }

        protected CancellationToken DisposalToken => this.disposalTokenSource.Token;

        public async Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.ReceivingStream != null, "No receiving stream.");

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                using (await this.receivingSemaphore.EnterAsync(cts.Token).ConfigureAwait(false))
                {
                    return await this.ReadCoreAsync(cts.Token).ConfigureAwait(false);
                }
            }
        }

        public async Task WriteAsync(string content, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));
            Verify.Operation(this.SendingStream != null, "No sending stream.");

            // Capture Encoding as a local since it may change over the time of this method's execution.
            Encoding contentEncoding = this.Encoding;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                using (await this.sendingSemaphore.EnterAsync(cts.Token).ConfigureAwait(false))
                {
                    await this.WriteCoreAsync(content, contentEncoding, cts.Token).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            if (!this.disposalTokenSource.IsCancellationRequested)
            {
                this.disposalTokenSource.Cancel();
                this.Dispose(true);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.ReceivingStream?.Dispose();
                this.SendingStream?.Dispose();
                this.sendingSemaphore.Dispose();
                this.receivingSemaphore.Dispose();
            }
        }

        protected abstract Task<string> ReadCoreAsync(CancellationToken cancellationToken);

        protected abstract Task WriteCoreAsync(string content, Encoding contentEncoding, CancellationToken cancellationToken);
    }
}
