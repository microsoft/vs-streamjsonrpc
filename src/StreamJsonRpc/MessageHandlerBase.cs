// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An abstract base class for for sending and receiving messages.
    /// </summary>
    /// <remarks>
    /// This class and its derivatives are safe to call from any thread.
    /// Calls to <see cref="WriteAsync(JsonRpcMessage, CancellationToken)"/>
    /// are protected by a semaphore to guarantee message integrity
    /// and may be made from any thread.
    /// The caller must take care to call <see cref="ReadAsync(CancellationToken)"/> sequentially.
    /// </remarks>
    public abstract class MessageHandlerBase : IJsonRpcMessageHandler, IDisposableObservable
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
        /// Initializes a new instance of the <see cref="MessageHandlerBase"/> class.
        /// </summary>
        /// <param name="formatter">The formatter used to serialize messages.</param>
        public MessageHandlerBase(IJsonRpcMessageFormatter formatter)
        {
            Requires.NotNull(formatter, nameof(formatter));
            this.Formatter = formatter;
        }

        /// <summary>
        /// Gets a value indicating whether this message handler can receive messages.
        /// </summary>
        public abstract bool CanRead { get; }

        /// <summary>
        /// Gets a value indicating whether this message handler can send messages.
        /// </summary>
        public abstract bool CanWrite { get; }

        /// <inheritdoc/>
        public IJsonRpcMessageFormatter Formatter { get; }

        /// <summary>
        /// Gets a value indicating whether this instance has been disposed.
        /// </summary>
        bool IDisposableObservable.IsDisposed => this.DisposalToken.IsCancellationRequested;

        /// <summary>
        /// Gets a token that is canceled when this instance is disposed.
        /// </summary>
        protected CancellationToken DisposalToken => this.disposalTokenSource.Token;

        /// <summary>
        /// Gets a value indicating whether the transport allows flushing while writing more data.
        /// </summary>
        protected abstract bool CanFlushConcurrentlyWithOtherWrites { get; }

        /// <summary>
        /// Reads a distinct and complete message from the transport, waiting for one if necessary.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the read request.</param>
        /// <returns>The received message, or <c>null</c> if the underlying transport ends before beginning another message.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="CanRead"/> returns <c>false</c>.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown if the transport ends while reading a message.</exception>
        /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled before a new message is received.</exception>
        /// <remarks>
        /// Implementations may assume this method is never called before any async result
        /// from a prior call to this method has completed.
        /// </remarks>
        public async ValueTask<JsonRpcMessage> ReadAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.CanRead, "No receiving stream.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                try
                {
                    JsonRpcMessage result = await this.ReadCoreAsync(cts.Token).ConfigureAwait(false);
                    return result;
                }
                catch (InvalidOperationException ex) when (cancellationToken.IsCancellationRequested)
                {
                    // PipeReader.ReadAsync can throw InvalidOperationException in a race where PipeReader.Complete() has been
                    // called but we haven't noticed the CancellationToken was canceled yet.
                    throw new OperationCanceledException("Reading failed during cancellation.", ex, cancellationToken);
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
        /// Writes a message to the transport and flushes.
        /// </summary>
        /// <param name="content">The message to write.</param>
        /// <param name="cancellationToken">A token to cancel the write request.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="CanWrite"/> returns <c>false</c>.</exception>
        /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled before message transmission begins.</exception>
        /// <remarks>
        /// Implementations should expect this method to be invoked concurrently
        /// and use a queue to preserve message order as they are transmitted one at a time.
        /// </remarks>
        public async ValueTask WriteAsync(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));
            Verify.Operation(this.CanWrite, "No sending stream.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(this.DisposalToken, cancellationToken))
            {
                try
                {
                    ValueTask flushTask;
                    using (await this.sendingSemaphore.EnterAsync(cts.Token).ConfigureAwait(false))
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        await this.WriteCoreAsync(content, cts.Token).ConfigureAwait(false);
                        flushTask = this.FlushAsync(cts.Token);

                        if (!this.CanFlushConcurrentlyWithOtherWrites)
                        {
                            await flushTask.ConfigureAwait(false);
                        }
                    }

                    if (this.CanFlushConcurrentlyWithOtherWrites)
                    {
                        await flushTask.ConfigureAwait(false);
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

        /// <summary>
        /// Disposes this instance, and cancels any pending read or write operations.
        /// </summary>
        public void Dispose()
        {
            if (!this.disposalTokenSource.IsCancellationRequested)
            {
                this.disposalTokenSource.Cancel();
                this.Dispose(true);
                GC.SuppressFinalize(this);
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
                this.sendingSemaphore.Dispose();
            }
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix

        /// <summary>
        /// Reads a distinct and complete message, waiting for one if necessary.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the read request.</param>
        /// <returns>
        /// A task whose result is the received message.
        /// A null string indicates the stream has ended.
        /// An empty string should never be returned.
        /// </returns>
        protected abstract ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Writes a message.
        /// </summary>
        /// <param name="content">The message to write.</param>
        /// <param name="cancellationToken">A token to cancel the transmission.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        protected abstract ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken);

        /// <summary>
        /// Ensures that all messages transmitted up to this point are en route to their destination,
        /// rather than sitting in some local buffer.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>
        /// A <see cref="Task"/> that completes when the write buffer has been transmitted,
        /// or at least that the operation is in progress, if final transmission cannot be tracked.
        /// </returns>
        protected abstract ValueTask FlushAsync(CancellationToken cancellationToken);

#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
    }
}
