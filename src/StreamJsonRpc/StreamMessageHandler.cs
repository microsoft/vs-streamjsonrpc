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
    /// An abstract base class for for sending and receiving messages over a
    /// reading and writing pair of <see cref="Stream"/> objects.
    /// </summary>
    /// <remarks>
    /// This class and its derivatives are safe to call from any thread.
    /// Read and write requests are protected by a semaphore to guarantee message integrity
    /// and may be made from any thread.
    /// </remarks>
    public abstract class StreamMessageHandler : IMessageHandler, IDisposableObservable
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
        /// The character encoding to use for the transmitted content.
        /// </summary>
        private Encoding encoding;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <param name="encoding">The character encoding to use when transmitting messages.</param>
        protected StreamMessageHandler(Stream sendingStream, Stream receivingStream, Encoding encoding)
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
                    JToken result = await this.ReadCoreAsync(cts.Token).ConfigureAwait(false);
                    return result;
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

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                try
                {
                    using (await this.sendingSemaphore.EnterAsync(cts.Token).ConfigureAwait(false))
                    {
                        await this.WriteCoreAsync(content, cts.Token).ConfigureAwait(false);
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
        /// <param name="cancellationToken">A token to cancel the transmission.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract ValueTask WriteCoreAsync(JToken content, CancellationToken cancellationToken);

        /// <summary>
        /// Calls <see cref="Stream.FlushAsync()"/> on the <see cref="SendingStream"/>,
        /// or equivalent sending stream if using an alternate transport.
        /// </summary>
        /// <returns>A <see cref="Task"/> that completes when the write buffer has been transmitted.</returns>
        protected virtual ValueTask FlushCoreAsync() => new ValueTask(this.SendingStream.FlushAsync());
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
    }
}
