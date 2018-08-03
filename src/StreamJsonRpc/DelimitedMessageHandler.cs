// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// An abstract base class for for sending and receiving distinct string messages
    /// over a channel that provides no natural boundaries and no built-in character encoding.
    /// </summary>
    /// <remarks>
    /// This class and its derivates are safe to call from any thread.
    /// Read and write requests are protected by a semaphore to guarantee message integrity
    /// and may be made from any thread.
    /// </remarks>
    public abstract class DelimitedMessageHandler : IMessageHandler, IDisposableObservable
    {
        /// <summary>
        /// The source of a token that is canceled when this instance is disposed.
        /// </summary>
        private readonly CancellationTokenSource disposalTokenSource = new CancellationTokenSource();

        /// <summary>
        /// A semaphore acquired while sending a message.
        /// </summary>
        private readonly AsyncSemaphore sendingSemaphore = new AsyncSemaphore(1);

        /// <summary>
        /// A semaphore acquired while receiving a message.
        /// </summary>
        private readonly AsyncSemaphore receivingSemaphore = new AsyncSemaphore(1);

        /// <summary>
        /// A temporary buffer used to serialize a <see cref="JToken"/>. Lazily initialized.
        /// </summary>
        private MemoryStream sendingContentBufferStream;

        /// <summary>
        /// A recycled <see cref="StreamWriter"/> used to write to <see cref="sendingContentBufferStream"/>. Lazily initialized.
        /// </summary>
        private StreamWriter sendingContentBufferStreamWriter;

        /// <summary>
        /// A recycled <see cref="JsonWriter"/> used to write to <see cref="sendingContentBufferStreamWriter"/>. Lazily initialized.
        /// </summary>
        private JsonWriter sendingContentBufferStreamJsonWriter;

        /// <summary>
        /// The character encoding to use for the transmitted content.
        /// </summary>
        private Encoding encoding;

        /// <summary>
        /// Initializes a new instance of the <see cref="DelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <param name="encoding">The character encoding to use when transmitting messages.</param>
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

        /// <summary>
        /// Gets a value indicating whether this message handler has a receiving stream.
        /// </summary>
        public bool CanRead => this.ReceivingStream != null;

        /// <summary>
        /// Gets a value indicating whether this message handler has a sending stream.
        /// </summary>
        public bool CanWrite => this.SendingStream != null;

        /// <inheritdoc />
        bool IDisposableObservable.IsDisposed => this.DisposalToken.IsCancellationRequested;

        /// <summary>
        /// Gets the stream used to transmit messages. May be null.
        /// </summary>
        protected Stream SendingStream { get; }

        /// <summary>
        /// Gets the stream used to receive messages. May be null.
        /// </summary>
        protected Stream ReceivingStream { get; }

        /// <summary>
        /// Gets a token that is canceled when this instance is disposed.
        /// </summary>
        protected CancellationToken DisposalToken => this.disposalTokenSource.Token;

        /// <inheritdoc />
        public async ValueTask<JToken> ReadAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.ReceivingStream != null, "No receiving stream.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                try
                {
                    using (await this.receivingSemaphore.EnterAsync(cts.Token).ConfigureAwait(false))
                    {
                        JToken result = await this.ReadCoreAsync(cts.Token).ConfigureAwait(false);
                        return result;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // If already canceled, throw that instead of ObjectDisposedException.
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask WriteAsync(JToken content, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));
            Verify.Operation(this.SendingStream != null, "No sending stream.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            // Capture Encoding as a local since it may change over the time of this method's execution.
            Encoding contentEncoding = this.Encoding;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                try
                {
                    using (await this.sendingSemaphore.EnterAsync(cts.Token).ConfigureAwait(false))
                    {
                        await this.WriteCoreAsync(content, contentEncoding, cts.Token).ConfigureAwait(false);
                    }

                    await this.FlushCoreAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // If already canceled, throw that instead of ObjectDisposedException.
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }

        /// <summary>
        /// Disposes this instance, and cancels any pending read or write operations.
        /// </summary>
        public void Dispose()
        {
            if (!this.disposalTokenSource.IsCancellationRequested)
            {
                this.disposalTokenSource.Cancel();
                this.Dispose(true);
            }
        }

        /// <summary>
        /// Disposes resources allocated by this instance.
        /// </summary>
        /// <param name="disposing"><c>true</c> when being disposed; <c>false</c> when being finalized.</param>
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

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        /// <summary>
        /// Reads a distinct and complete message from the stream, waiting for one if necessary.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the read request.</param>
        /// <returns>
        /// A task whose result is the received message.
        /// A null string indicates the stream has ended.
        /// An empty string should never be returned.
        /// </returns>
        protected abstract ValueTask<JToken> ReadCoreAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Writes a message to the stream.
        /// </summary>
        /// <param name="content">The message to write.</param>
        /// <param name="contentEncoding">The encoding to use for <paramref name="content"/>.</param>
        /// <param name="cancellationToken">A token to cancel the transmission.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract ValueTask WriteCoreAsync(JToken content, Encoding contentEncoding, CancellationToken cancellationToken);

        /// <summary>
        /// Calls <see cref="Stream.FlushAsync()"/> on the <see cref="SendingStream"/>,
        /// or equivalent sending stream if using an alternate transport.
        /// </summary>
        /// <returns>A <see cref="Task"/> that completes when the write buffer has been transmitted.</returns>
        protected virtual ValueTask FlushCoreAsync() => new ValueTask(this.SendingStream.FlushAsync());
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix

        /// <summary>
        /// Serializes a <see cref="JToken"/> to a memory stream and returns the result.
        /// </summary>
        /// <param name="content">The <see cref="JToken"/> to serialize.</param>
        /// <param name="encoding">The text encoding to use.</param>
        /// <returns>A <see cref="MemoryStream"/> with the serialized content, positioned at 0.</returns>
        /// <remarks>
        /// The returned <see cref="MemoryStream"/> is recycled for each call. This method should *not* be invoked
        /// until any prior invocation's result is no longer necessary.
        /// </remarks>
        protected MemoryStream Serialize(JToken content, Encoding encoding)
        {
            Requires.NotNull(content, nameof(content));
            Requires.NotNull(encoding, nameof(encoding));

            if (this.sendingContentBufferStream == null)
            {
                this.sendingContentBufferStream = new MemoryStream(4 * 1024);
            }
            else
            {
                this.sendingContentBufferStream.SetLength(0);
            }

            if (this.sendingContentBufferStreamWriter == null || this.sendingContentBufferStreamWriter.Encoding != encoding)
            {
                this.sendingContentBufferStreamWriter = new StreamWriter(this.sendingContentBufferStream, encoding);
                this.sendingContentBufferStreamJsonWriter = null; // we'll need to reinitialize this for the new StreamWriter
            }

            if (this.sendingContentBufferStreamJsonWriter == null)
            {
                this.sendingContentBufferStreamJsonWriter = new JsonTextWriter(this.sendingContentBufferStreamWriter);
            }

            content.WriteTo(this.sendingContentBufferStreamJsonWriter);
            this.sendingContentBufferStreamWriter.Flush(); // this flushes the internal encoder so it's safe to reuse
            this.sendingContentBufferStream.Position = 0;

            return this.sendingContentBufferStream;
        }
    }
}
