// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipelines;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Nerdbank.Streams;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// An abstract base class for for sending and receiving messages
    /// using <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
    /// </summary>
    /// <remarks>
    /// This class and its derivatives are safe to call from any thread.
    /// Read and write requests are protected by a semaphore to guarantee message integrity
    /// and may be made from any thread.
    /// </remarks>
    public abstract class PipeMessageHandler : IMessageHandler, IDisposableObservable
    {
        /// <summary>
        /// A semaphore acquired while sending a message.
        /// </summary>
        private readonly AsyncSemaphore sendingSemaphore = new AsyncSemaphore(1);

        /// <summary>
        /// Backing field for the <see cref="Encoding"/> property.
        /// </summary>
        private Encoding encoding;

        /// <summary>
        /// Objects that we should dispose when we are disposed. May be null.
        /// </summary>
        private List<IDisposable> disposables;

        /// <summary>
        /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
        /// </summary>
        /// <param name="pipe">The reader and writer to use for receiving/transmitting messages.</param>
        /// <param name="encoding">The encoding to use for transmitted messages.</param>
        public PipeMessageHandler(IDuplexPipe pipe, Encoding encoding)
            : this(Requires.NotNull(pipe, nameof(pipe)).Output, Requires.NotNull(pipe, nameof(pipe)).Input, encoding)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The writer to use for transmitting messages.</param>
        /// <param name="reader">The reader to use for receiving messages.</param>
        /// <param name="encoding">The encoding to use for transmitted messages.</param>
        public PipeMessageHandler(PipeWriter writer, PipeReader reader, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            this.encoding = encoding;
            this.Reader = reader;
            this.Writer = writer;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The stream to use for transmitting messages.</param>
        /// <param name="reader">The stream to use for receiving messages.</param>
        /// <param name="encoding">The encoding to use for transmitted messages.</param>
        public PipeMessageHandler(Stream writer, Stream reader, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            this.encoding = encoding;

            // We use Strict reader to avoid max buffer size issues in Pipe (https://github.com/dotnet/corefx/issues/30689)
            // since it's just stream semantics.
            this.Reader = reader?.UseStrictPipeReader();
            this.Writer = writer?.UsePipeWriter();

            this.disposables = new List<IDisposable>();
            if (reader != null)
            {
                this.disposables.Add(reader);
            }

            if (writer != null && writer != reader)
            {
                this.disposables.Add(writer);
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

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

        /// <inheritdoc/>
        public bool CanRead => this.Reader != null;

        /// <inheritdoc/>
        public bool CanWrite => this.Writer != null;

        /// <summary>
        /// Gets the reader to use for receiving messages.
        /// </summary>
        protected PipeReader Reader { get; }

        /// <summary>
        /// Gets the writer to use for transmitting messages.
        /// </summary>
        protected PipeWriter Writer { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        /// <inheritdoc/>
        public virtual ValueTask<JToken> ReadAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.CanRead, "No input pipe.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            try
            {
                return this.ReadCoreAsync(cancellationToken);
            }
            catch (InvalidOperationException ex) when (cancellationToken.IsCancellationRequested)
            {
                // PipeReader.ReadAsync can throw InvalidOperationException in a race where PipeReader.Complete() has been
                // called but we haven't noticed the CancellationToken was canceled yet.
                throw new OperationCanceledException("Reading failed during cancellation.", ex, cancellationToken);
            }
        }

        /// <inheritdoc/>
        public virtual async ValueTask WriteAsync(JToken json, CancellationToken cancellationToken)
        {
            Requires.NotNull(json, nameof(json));
            Verify.Operation(this.CanWrite, "No output pipe.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            try
            {
                using (await this.sendingSemaphore.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    this.Write(json, cancellationToken);
                    await this.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // If already canceled, throw that instead of ObjectDisposedException.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        /// <summary>
        /// Writes a message to the pipe.
        /// </summary>
        /// <param name="content">The message to write.</param>
        /// <param name="cancellationToken">A token to cancel the transmission.</param>
        /// <remarks>
        /// Implementations may assume the method is never called before the previous call has completed.
        /// They can assume their caller will invoke <see cref="PipeWriter.FlushAsync(CancellationToken)"/> on their behalf
        /// after writing is completed.
        /// </remarks>
        protected abstract void Write(JToken content, CancellationToken cancellationToken);

        /// <summary>
        /// Reads a message from the pipe.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the read.</param>
        /// <returns>A task that represents the asynchronous read operation.</returns>
        /// <remarks>
        /// Implementations may assume the method is never called before the previous call has completed.
        /// </remarks>
        protected abstract ValueTask<JToken> ReadCoreAsync(CancellationToken cancellationToken);
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix

        /// <summary>
        /// Disposes of managed and/or native resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to dispose of native resources also.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.IsDisposed = true;
                this.sendingSemaphore.Dispose();
                this.Reader?.Complete();
                this.Writer?.Complete();

                if (this.disposables != null)
                {
                    // Only dispose the underlying streams (if any) *after* our writer's work has been fully read.
                    // Otherwise we risk cutting of data that we claimed to have transmitted.
                    if (this.Writer != null && this.disposables != null)
                    {
                        this.Writer.OnReaderCompleted((ex, s) => this.DisposeDisposables(), null);
                    }
                    else
                    {
                        this.DisposeDisposables();
                    }
                }
            }
        }

        private void DisposeDisposables()
        {
            if (this.disposables != null)
            {
                foreach (IDisposable disposable in this.disposables)
                {
                    disposable?.Dispose();
                }

                this.disposables = null;
            }
        }
    }
}
