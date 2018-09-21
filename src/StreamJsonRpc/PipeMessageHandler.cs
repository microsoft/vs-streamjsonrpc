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
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An abstract base class for for sending and receiving messages
    /// using <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
    /// </summary>
    public abstract class PipeMessageHandler : MessageHandlerBase
    {
        /// <summary>
        /// Objects that we should dispose when we are disposed. May be null.
        /// </summary>
        private List<IDisposable> disposables;

        /// <summary>
        /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
        /// </summary>
        /// <param name="pipe">The reader and writer to use for receiving/transmitting messages.</param>
        /// <param name="formatter">The formatter used to serialize messages.</param>
        public PipeMessageHandler(IDuplexPipe pipe, IJsonRpcMessageFormatter formatter)
            : this(Requires.NotNull(pipe, nameof(pipe)).Output, Requires.NotNull(pipe, nameof(pipe)).Input, formatter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The writer to use for transmitting messages.</param>
        /// <param name="reader">The reader to use for receiving messages.</param>
        /// <param name="formatter">The formatter used to serialize messages.</param>
        public PipeMessageHandler(PipeWriter writer, PipeReader reader, IJsonRpcMessageFormatter formatter)
            : base(formatter)
        {
            this.Reader = reader;
            this.Writer = writer;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The stream to use for transmitting messages.</param>
        /// <param name="reader">The stream to use for receiving messages.</param>
        /// <param name="formatter">The formatter used to serialize messages.</param>
        public PipeMessageHandler(Stream writer, Stream reader, IJsonRpcMessageFormatter formatter)
            : base(formatter)
        {
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

        /// <inheritdoc/>
        public override bool CanRead => this.Reader != null;

        /// <inheritdoc/>
        public override bool CanWrite => this.Writer != null;

        /// <summary>
        /// Gets the reader to use for receiving messages.
        /// </summary>
        protected PipeReader Reader { get; }

        /// <summary>
        /// Gets the writer to use for transmitting messages.
        /// </summary>
        protected PipeWriter Writer { get; }

        /// <inheritdoc/>
        protected override bool CanFlushConcurrentlyWithOtherWrites => false;

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        /// <inheritdoc/>
        protected sealed override ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            this.Write(content, cancellationToken);
            return default;
        }
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix

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
        protected abstract void Write(JsonRpcMessage content, CancellationToken cancellationToken);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
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

                base.Dispose(disposing);
            }
        }

        /// <inheritdoc />
        protected override async ValueTask FlushAsync(CancellationToken cancellationToken) => await this.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Reads from the <see cref="Reader"/> until at least a specified number of bytes are available.
        /// </summary>
        /// <param name="requiredBytes">The number of bytes that must be available.</param>
        /// <param name="allowEmpty"><c>true</c> to allow returning 0 bytes if the end of the stream is encountered before any bytes are read.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The <see cref="ReadResult"/> containing at least <paramref name="requiredBytes"/> bytes.</returns>
        /// <exception cref="OperationCanceledException">Thrown if <see cref="ReadResult.IsCanceled"/>.</exception>
        /// <exception cref="EndOfStreamException">
        /// Thrown if <see cref="ReadResult.IsCompleted"/> before we have <paramref name="requiredBytes"/> bytes.
        /// Not thrown if 0 bytes were read and <paramref name="allowEmpty"/> is <c>true</c>.
        /// </exception>
        protected async ValueTask<ReadResult> ReadAtLeastAsync(int requiredBytes, bool allowEmpty, CancellationToken cancellationToken)
        {
            var readResult = await this.Reader.ReadAsync(cancellationToken);
            while (readResult.Buffer.Length < requiredBytes && !readResult.IsCompleted && !readResult.IsCanceled)
            {
                this.Reader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
                readResult = await this.Reader.ReadAsync(cancellationToken);
            }

            if (allowEmpty && readResult.Buffer.Length == 0)
            {
                return readResult;
            }

            if (readResult.Buffer.Length < requiredBytes)
            {
                throw readResult.IsCompleted ? new EndOfStreamException() :
                    readResult.IsCanceled ? new OperationCanceledException() :
                    Assumes.NotReachable();
            }

            return readResult;
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
