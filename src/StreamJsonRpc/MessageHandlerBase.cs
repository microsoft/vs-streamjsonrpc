// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
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
    public abstract class MessageHandlerBase : IJsonRpcMessageHandler, IDisposableObservable, Microsoft.VisualStudio.Threading.IAsyncDisposable
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
        /// The sync object to lock when inspecting and mutating the <see cref="state"/> field.
        /// </summary>
        private readonly object syncObject = new object();

        /// <summary>
        /// A signal that the last read operation has completed.
        /// </summary>
        private readonly AsyncManualResetEvent readingCompleted = new AsyncManualResetEvent();

        /// <summary>
        /// A signal that the last write operation has completed.
        /// </summary>
        private readonly AsyncManualResetEvent writingCompleted = new AsyncManualResetEvent();

        /// <summary>
        /// A value indicating whether the <see cref="ReadAsync(CancellationToken)"/> and/or <see cref="WriteAsync(JsonRpcMessage, CancellationToken)"/> methods are in progress.
        /// </summary>
        private MessageHandlerState state;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageHandlerBase"/> class.
        /// </summary>
        /// <param name="formatter">The formatter used to serialize messages.</param>
        public MessageHandlerBase(IJsonRpcMessageFormatter formatter)
        {
            Requires.NotNull(formatter, nameof(formatter));
            this.Formatter = formatter;

            Task readerDisposal = this.readingCompleted.WaitAsync().ContinueWith((_, s) => ((MessageHandlerBase)s).DisposeReader(), this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Task writerDisposal = this.writingCompleted.WaitAsync().ContinueWith((_, s) => ((MessageHandlerBase)s).DisposeWriter(), this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            this.Completion = Task.WhenAll(readerDisposal, writerDisposal).ContinueWith((_, s) => ((MessageHandlerBase)s).Dispose(true), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        [Flags]
        private enum MessageHandlerState
        {
            /// <summary>
            /// No flags.
            /// </summary>
            None = 0,

            /// <summary>
            /// Indicates that the <see cref="WriteAsync(JsonRpcMessage, CancellationToken)"/> method is in running.
            /// </summary>
            Writing = 0x1,

            /// <summary>
            /// Indicates that the <see cref="ReadAsync(CancellationToken)"/> method is in running.
            /// </summary>
            Reading = 0x2,
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
        /// Gets a task that completes when this instance has completed disposal.
        /// </summary>
        private Task Completion { get; }

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
        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.CanRead, "No receiving stream.");
            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);
            this.SetState(MessageHandlerState.Reading);

            try
            {
                JsonRpcMessage? result = await this.ReadCoreAsync(cancellationToken).ConfigureAwait(false);
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
            finally
            {
                lock (this.syncObject)
                {
                    this.state &= ~MessageHandlerState.Reading;
                    if (this.DisposalToken.IsCancellationRequested)
                    {
                        this.readingCompleted.Set();
                    }
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

            try
            {
                using (await this.sendingSemaphore.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    this.SetState(MessageHandlerState.Writing);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await this.WriteCoreAsync(content, cancellationToken).ConfigureAwait(false);
                        await this.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (this.syncObject)
                        {
                            this.state &= ~MessageHandlerState.Writing;
                            if (this.DisposalToken.IsCancellationRequested)
                            {
                                this.writingCompleted.Set();
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // If already canceled, throw that instead of ObjectDisposedException.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

#pragma warning disable VSTHRD002 // We synchronously block, but nothing here should ever require the main thread.
        /// <summary>
        /// Disposes this instance, and cancels any pending read or write operations.
        /// </summary>
        [Obsolete("Call IAsyncDisposable.DisposeAsync instead.")]
        public void Dispose() => this.DisposeAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        /// <summary>
        /// Disposes this instance, and cancels any pending read or write operations.
        /// </summary>
        public virtual async Task DisposeAsync()
        {
            if (!this.disposalTokenSource.IsCancellationRequested)
            {
                this.disposalTokenSource.Cancel();

                // Kick off disposal of reading and/or writing resources based on whether they're active right now or not.
                // If they're active, they'll take care of themselves when they finish since we signaled disposal.
                lock (this.syncObject)
                {
                    if (!this.state.HasFlag(MessageHandlerState.Reading))
                    {
                        this.readingCompleted.Set();
                    }

                    if (!this.state.HasFlag(MessageHandlerState.Writing))
                    {
                        this.writingCompleted.Set();
                    }
                }

                // Wait for completion to actually complete, and re-throw any exceptions.
                await this.Completion.ConfigureAwait(false);

                this.Dispose(true);
            }
        }

        /// <summary>
        /// Disposes resources allocated by this instance that are common to both reading and writing.
        /// </summary>
        /// <param name="disposing"><c>true</c> when being disposed; <c>false</c> when being finalized.</param>
        /// <remarks>
        /// <para>
        /// This method is called by <see cref="DisposeAsync"/> after both <see cref="DisposeReader"/> and <see cref="DisposeWriter"/> have completed.
        /// </para>
        /// <para>Overrides of this method *should* call the base method as well.</para>
        /// </remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                (this.Formatter as IDisposable)?.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Disposes resources allocated by this instance that are used for reading (not writing).
        /// </summary>
        /// <remarks>
        /// This method is called by <see cref="MessageHandlerBase"/> after the last read operation has completed.
        /// </remarks>
        protected virtual void DisposeReader()
        {
        }

        /// <summary>
        /// Disposes resources allocated by this instance that are used for writing (not reading).
        /// </summary>
        /// <remarks>
        /// This method is called by <see cref="MessageHandlerBase"/> after the last write operation has completed.
        /// Overrides of this method *should* call the base method as well.
        /// </remarks>
        protected virtual void DisposeWriter()
        {
            this.sendingSemaphore.Dispose();
        }

        /// <summary>
        /// Reads a distinct and complete message, waiting for one if necessary.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the read request.</param>
        /// <returns>
        /// A task whose result is the received message.
        /// A null string indicates the stream has ended.
        /// An empty string should never be returned.
        /// </returns>
        protected abstract ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken);

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

        private void SetState(MessageHandlerState startingOperation)
        {
            lock (this.syncObject)
            {
                Verify.NotDisposed(this);
                MessageHandlerState state = this.state;
                Assumes.False(state.HasFlag(startingOperation));
                this.state |= startingOperation;
            }
        }
    }
}
