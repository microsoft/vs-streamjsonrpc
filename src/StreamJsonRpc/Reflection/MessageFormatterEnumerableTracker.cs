// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;
using StreamJsonRpc.Protocol;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// A helper class that <see cref="IJsonRpcMessageFormatter"/> implementations may use to support <see cref="IAsyncEnumerable{T}"/> return values from RPC methods.
/// </summary>
public class MessageFormatterEnumerableTracker
{
    /// <summary>
    /// The name of the string property that carries the handle for the enumerable.
    /// </summary>
    public const string TokenPropertyName = "token";

    /// <summary>
    /// The name of the JSON array property that contains the values.
    /// </summary>
    public const string ValuesPropertyName = "values";

    /// <summary>
    /// The name of the boolean property that indicates whether the last value has been returned to the consumer.
    /// </summary>
    private const string FinishedPropertyName = "finished";

    private const string NextMethodName = "$/enumerator/next";
    private const string DisposeMethodName = "$/enumerator/abort";

    private static readonly MethodInfo OnNextAsyncMethodInfo = typeof(MessageFormatterEnumerableTracker).GetMethod(nameof(OnNextAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo OnDisposeAsyncMethodInfo = typeof(MessageFormatterEnumerableTracker).GetMethod(nameof(OnDisposeAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Dictionary used to map the outbound request id to the list of tokens that track <see cref="IAsyncEnumerable{T}"/> state machines it owns
    /// so that the state machines are cleaned after getting the final response.
    /// </summary>
    /// <remarks>
    /// Note that we only track OUTBOUND REQUESTS that carry enumerables here.
    /// OUTBOUND RESPONSES that carry enumerables are not tracked except in <see cref="generatorsByToken"/>.
    /// This means that responses that carry enumerables will not be cleaned up if the response is never processed by the client
    /// until the connection dies.
    /// </remarks>
    private readonly Dictionary<RequestId, ImmutableList<long>> generatorTokensByRequestId = new Dictionary<RequestId, ImmutableList<long>>();

    private readonly Dictionary<long, IGeneratingEnumeratorTracker> generatorsByToken = new Dictionary<long, IGeneratingEnumeratorTracker>();

    private readonly JsonRpc jsonRpc;
    private readonly IJsonRpcFormatterState formatterState;
    private readonly MessageFormatterRpcMarshaledContextTracker? rpcTracker;
    private readonly object syncObject = new object();
    private long nextToken;

    /// <inheritdoc cref="MessageFormatterEnumerableTracker(JsonRpc, IJsonRpcFormatterState, MessageFormatterRpcMarshaledContextTracker?)"/>
    public MessageFormatterEnumerableTracker(JsonRpc jsonRpc, IJsonRpcFormatterState formatterState)
        : this(jsonRpc, formatterState, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageFormatterEnumerableTracker"/> class.
    /// </summary>
    /// <param name="jsonRpc">The <see cref="JsonRpc"/> instance that may be used to send or receive RPC messages related to <see cref="IAsyncEnumerable{T}"/>.</param>
    /// <param name="formatterState">The formatter that owns this tracker.</param>
    /// <param name="rpcTracker">The RPC marshalable object support used by the formatter, if applicable.</param>
    internal MessageFormatterEnumerableTracker(JsonRpc jsonRpc, IJsonRpcFormatterState formatterState, MessageFormatterRpcMarshaledContextTracker? rpcTracker)
    {
        Requires.NotNull(jsonRpc, nameof(jsonRpc));
        Requires.NotNull(formatterState, nameof(formatterState));

        this.jsonRpc = jsonRpc;
        this.formatterState = formatterState;
        this.rpcTracker = rpcTracker;
        jsonRpc.AddLocalRpcMethod(NextMethodName, OnNextAsyncMethodInfo, this);
        jsonRpc.AddLocalRpcMethod(DisposeMethodName, OnDisposeAsyncMethodInfo, this);
        this.formatterState = formatterState;

        // We don't offer a way to remove these handlers because this object should has a lifetime closely tied to the JsonRpc object anyway.
        IJsonRpcFormatterCallbacks callbacks = jsonRpc;
        callbacks.RequestTransmissionAborted += (s, e) => this.CleanUpResources(e.RequestId);
        callbacks.ResponseReceived += (s, e) => this.CleanUpResources(e.RequestId);
    }

    private interface IGeneratingEnumeratorTracker : System.IAsyncDisposable
    {
        ValueTask<object> GetNextValuesAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Checks if a given <see cref="Type"/> implements <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <param name="objectType">The type which may implement <see cref="IAsyncEnumerable{T}"/>.</param>
    /// <returns>true if given <see cref="Type"/> implements <see cref="IAsyncEnumerable{T}"/>; otherwise, false.</returns>
    /// <devremarks>
    /// We use <see langword="int"/> as a generic type argument in this because what we use doesn't matter, but we must use *something*.
    /// </devremarks>
    public static bool CanSerialize(Type objectType) => TrackerHelpers<IAsyncEnumerable<int>>.CanSerialize(objectType);

    /// <summary>
    /// Checks if a given <see cref="Type"/> is exactly some closed generic type based on <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <param name="objectType">The type which may be <see cref="IAsyncEnumerable{T}"/>.</param>
    /// <returns>true if given <see cref="Type"/> is <see cref="IAsyncEnumerable{T}"/>; otherwise, false.</returns>
    /// <devremarks>
    /// We use <see langword="int"/> as a generic type argument in this because what we use doesn't matter, but we must use *something*.
    /// </devremarks>
    public static bool CanDeserialize(Type objectType) => TrackerHelpers<IAsyncEnumerable<int>>.CanDeserialize(objectType);

    /// <summary>
    /// Used by the generator to assign a handle to the given <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of value that is produced by the enumerable.</typeparam>
    /// <param name="enumerable">The enumerable to assign a handle to.</param>
    /// <returns>The handle that was assigned.</returns>
    public long GetToken<T>(IAsyncEnumerable<T> enumerable)
    {
        Requires.NotNull(enumerable, nameof(enumerable));
        if (this.formatterState.SerializingMessageWithId.IsEmpty)
        {
            throw new NotSupportedException(Resources.MarshaledObjectInNotificationError);
        }

        long handle = Interlocked.Increment(ref this.nextToken);
        lock (this.syncObject)
        {
            // We only track the token if we are serializing a request, since per our documentation,
            // we forcibly terminate the enumerable at the client side when the request has been responded to.
            // Storing request IDs for outbound *responses* that carry enumerables would lead to them being disposed of
            // when an INBOUND response with the same ID is received.
            if (this.formatterState.SerializingRequest)
            {
                if (!this.generatorTokensByRequestId.TryGetValue(this.formatterState.SerializingMessageWithId, out ImmutableList<long>? tokens))
                {
                    tokens = ImmutableList<long>.Empty;
                }

                this.generatorTokensByRequestId[this.formatterState.SerializingMessageWithId] = tokens.Add(handle);
            }

            this.generatorsByToken.Add(handle, new GeneratingEnumeratorTracker<T>(this, handle, enumerable, settings: enumerable.GetJsonRpcSettings()));
        }

        return handle;
    }

    /// <summary>
    /// Used by the consumer to construct a proxy that implements <see cref="IAsyncEnumerable{T}"/>
    /// and gets all its values from a remote generator.
    /// </summary>
    /// <typeparam name="T">The type of value that is produced by the enumerable.</typeparam>
    /// <param name="handle">The handle specified by the generator that is used to obtain more values or dispose of the enumerator. May be <see langword="null"/> to indicate there will be no more values.</param>
    /// <param name="prefetchedItems">The list of items that are included with the enumerable handle.</param>
    /// <returns>The enumerator.</returns>
#pragma warning disable VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
    public IAsyncEnumerable<T> CreateEnumerableProxy<T>(object? handle, IReadOnlyList<T>? prefetchedItems)
#pragma warning restore VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
    {
        IDisposable? requestResourcesDeferral = null;
        if (handle is not null && this.rpcTracker is not null && !this.formatterState.DeserializingMessageWithId.IsEmpty)
        {
            requestResourcesDeferral = this.rpcTracker.OutboundCleanupDeferral(this.formatterState.DeserializingMessageWithId);
        }

        return new AsyncEnumerableProxy<T>(this.jsonRpc, handle, prefetchedItems, requestResourcesDeferral);
    }

    private ValueTask<object> OnNextAsync(long token, CancellationToken cancellationToken)
    {
        IGeneratingEnumeratorTracker? generator;
        lock (this.syncObject)
        {
            if (!this.generatorsByToken.TryGetValue(token, out generator))
            {
                throw new LocalRpcException(Resources.UnknownTokenToMarshaledObject) { ErrorCode = (int)JsonRpcErrorCode.NoMarshaledObjectFound };
            }
        }

        return generator.GetNextValuesAsync(cancellationToken);
    }

    private ValueTask OnDisposeAsync(long token)
    {
        IGeneratingEnumeratorTracker? generator;
        lock (this.syncObject)
        {
            if (!this.generatorsByToken.TryGetValue(token, out generator))
            {
                throw new LocalRpcException(Resources.UnknownTokenToMarshaledObject) { ErrorCode = (int)JsonRpcErrorCode.NoMarshaledObjectFound };
            }

            this.generatorsByToken.Remove(token);
        }

        return generator.DisposeAsync();
    }

    private void CleanUpResources(RequestId outboundRequestId)
    {
        lock (this.syncObject)
        {
            if (this.generatorTokensByRequestId.TryGetValue(outboundRequestId, out ImmutableList<long>? tokens))
            {
                foreach (var token in tokens)
                {
                    this.generatorsByToken.Remove(token);
                }

                this.generatorTokensByRequestId.Remove(outboundRequestId);
            }
        }
    }

    [DataContract]
    internal class EnumeratorResults<T>
    {
        [DataMember(Name = ValuesPropertyName, Order = 0)]
        [STJ.JsonPropertyName(ValuesPropertyName), STJ.JsonPropertyOrder(0)]
        [PropertyShape(Name = ValuesPropertyName, Order = 0)]
        public IReadOnlyList<T>? Values { get; set; }

        [DataMember(Name = FinishedPropertyName, Order = 1)]
        [STJ.JsonPropertyName(FinishedPropertyName), STJ.JsonPropertyOrder(1)]
        [PropertyShape(Name = FinishedPropertyName, Order = 1)]
        public bool Finished { get; set; }
    }

    private class GeneratingEnumeratorTracker<T> : IGeneratingEnumeratorTracker
    {
        private readonly IAsyncEnumerator<T> enumerator;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly BufferBlock<T>? readAheadElements;

        private readonly MessageFormatterEnumerableTracker tracker;

        private readonly long token;

        internal GeneratingEnumeratorTracker(MessageFormatterEnumerableTracker tracker, long token, IAsyncEnumerable<T> enumerable, JsonRpcEnumerableSettings settings)
        {
            this.tracker = tracker;
            this.token = token;
            this.enumerator = enumerable.GetAsyncEnumerator(this.cancellationTokenSource.Token);
            this.Settings = settings;

            if (settings.MaxReadAhead > 0)
            {
                this.readAheadElements = new BufferBlock<T>(new DataflowBlockOptions { BoundedCapacity = settings.MaxReadAhead, EnsureOrdered = true });
                this.ReadAheadAsync().Forget(); // exceptions fault the buffer block
            }
        }

        internal JsonRpcEnumerableSettings Settings { get; }

        public async ValueTask<object> GetNextValuesAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (cancellationToken.Register(state => ((CancellationTokenSource)state!).Cancel(), this.cancellationTokenSource))
                {
                    cancellationToken = this.cancellationTokenSource.Token;
                    bool finished = false;
                    var results = new List<T>(this.Settings.MinBatchSize);
                    if (this.readAheadElements is not null)
                    {
                        // Fetch at least the min batch size and at most the number that has been cached up to this point (or until we hit the end of the sequence).
                        // We snap the number of cached elements up front because as we dequeue, we create capacity to store more and we don't want to
                        // collect and return more than MaxReadAhead.
                        int cachedOnEntry = this.readAheadElements.Count;
                        for (int i = 0; !this.readAheadElements.Completion.IsCompleted && (i < this.Settings.MinBatchSize || (cachedOnEntry - results.Count > 0)); i++)
                        {
                            try
                            {
                                T element = await this.readAheadElements.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                                results.Add(element);
                            }
                            catch (InvalidOperationException)
                            {
                                // Race condition. The sequence is over.
                                // Per https://github.com/microsoft/vs-streamjsonrpc/issues/867, we do *not* confirm this
                                // block has completed because in fact the completion signal can come in asynchronously.
                                // But per API documentation on the block, this exception should only be thrown in this case.
                                finished = true;
                                break;
                            }
                        }

                        if (this.readAheadElements.Completion.IsCompleted)
                        {
                            // Rethrow any exceptions.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                            await this.readAheadElements.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
                            finished = true;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < this.Settings.MinBatchSize; i++)
                        {
                            if (!await this.enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                finished = true;
                                break;
                            }

                            results.Add(this.enumerator.Current);
                        }
                    }

                    if (finished)
                    {
                        // Clean up all resources since we don't expect the client to send a dispose notification
                        // since finishing the enumeration implicitly should dispose of it.
                        await this.tracker.OnDisposeAsync(this.token).ConfigureAwait(false);
                    }

                    return new EnumeratorResults<T>
                    {
                        Finished = finished,
                        Values = results,
                    };
                }
            }
            catch
            {
                // An error is considered fatal to the enumerable, so clean up everything.
                await this.tracker.OnDisposeAsync(this.token).ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
#if NET8_0_OR_GREATER
            await this.cancellationTokenSource.CancelAsync().ConfigureAwait(false);
#else
            this.cancellationTokenSource.Cancel();
#endif
            this.readAheadElements?.Complete();
            await this.enumerator.DisposeAsync().ConfigureAwait(false);
        }

        private async Task ReadAheadAsync()
        {
            Assumes.NotNull(this.readAheadElements);
            try
            {
                while (await this.enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    await this.readAheadElements.SendAsync(this.enumerator.Current, this.cancellationTokenSource.Token).ConfigureAwait(false);
                }

                this.readAheadElements.Complete();
            }
            catch (Exception ex)
            {
                ITargetBlock<T> target = this.readAheadElements;
                target.Fault(ex);
            }
        }
    }

    /// <summary>
    /// Provides the <see cref="IAsyncEnumerable{T}"/> instance that is used by a consumer.
    /// </summary>
    /// <typeparam name="T">The type of value produced by the enumerator.</typeparam>
    private class AsyncEnumerableProxy<T> : IAsyncEnumerable<T>
    {
        private readonly JsonRpc jsonRpc;
        private readonly bool finished;
        private readonly IDisposable? requestResourcesDeferral;
        private object? handle;
        private bool enumeratorAcquired;
        private IReadOnlyList<T>? prefetchedItems;

        internal AsyncEnumerableProxy(JsonRpc jsonRpc, object? handle, IReadOnlyList<T>? prefetchedItems, IDisposable? requestResourcesDeferral)
        {
            this.jsonRpc = jsonRpc;
            this.handle = handle;
            this.prefetchedItems = prefetchedItems;
            this.requestResourcesDeferral = requestResourcesDeferral;
            this.finished = handle is null;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken)
        {
            Verify.Operation(!this.enumeratorAcquired, Resources.CannotBeCalledAfterGetAsyncEnumerator);
            this.enumeratorAcquired = true;
            var result = new AsyncEnumeratorProxy(this, this.handle, this.prefetchedItems, this.finished, this.requestResourcesDeferral, cancellationToken);
            this.prefetchedItems = null;
            return result;
        }

        /// <summary>
        /// Provides the <see cref="IAsyncEnumerator{T}"/> instance that is used by a consumer.
        /// </summary>
        private class AsyncEnumeratorProxy : IAsyncEnumerator<T>
        {
            private readonly AsyncEnumerableProxy<T> owner;
            private readonly CancellationToken cancellationToken;
            private readonly object[]? nextOrDisposeArguments;
            private readonly IDisposable? requestResourcesDeferral;

            /// <summary>
            /// A sequence of values that have already been received from the generator but not yet consumed.
            /// </summary>
            private Sequence<T> localCachedValues = new Sequence<T>();

            /// <summary>
            /// A value indicating whether the generator has reported that no more values will be forthcoming.
            /// </summary>
            private bool generatorReportsFinished;

            private bool moveNextCalled;

            private bool disposed;

            internal AsyncEnumeratorProxy(AsyncEnumerableProxy<T> owner, object? handle, IReadOnlyList<T>? prefetchedItems, bool finished, IDisposable? requestResourcesDeferral, CancellationToken cancellationToken)
            {
                this.owner = owner;
                this.nextOrDisposeArguments = handle is not null ? new object[] { handle } : null;
                this.cancellationToken = cancellationToken;

                if (prefetchedItems is not null)
                {
                    Write(this.localCachedValues, prefetchedItems);
                }

                this.generatorReportsFinished = finished;
                this.requestResourcesDeferral = requestResourcesDeferral;
            }

            public T Current
            {
                get
                {
                    Verify.NotDisposed(!this.disposed, this);
                    if (this.localCachedValues.Length == 0)
                    {
                        throw new InvalidOperationException("Call " + nameof(this.MoveNextAsync) + " first and confirm it returns true first.");
                    }

                    return this.localCachedValues.AsReadOnlySequence.First.Span[0];
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (!this.disposed)
                {
                    this.disposed = true;

                    // Recycle buffers
                    this.localCachedValues.Reset();

                    // Notify server if it wasn't already finished.
                    if (!this.generatorReportsFinished)
                    {
                        await this.owner.jsonRpc.NotifyAsync(DisposeMethodName, this.nextOrDisposeArguments).ConfigureAwait(false);
                    }

                    // Clean up any local resources that were held open for the remote source of the enumeration.
                    this.requestResourcesDeferral?.Dispose();
                }
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                Verify.NotDisposed(!this.disposed, this);

                // Consume one locally cached value, if we have one.
                if (this.localCachedValues.Length > 0)
                {
                    if (this.moveNextCalled)
                    {
                        this.localCachedValues.AdvanceTo(this.localCachedValues.AsReadOnlySequence.GetPosition(1));
                    }
                    else
                    {
                        // Don't consume one the first time we're called if we have an initial set of values.
                        this.moveNextCalled = true;
                        return true;
                    }
                }

                this.moveNextCalled = true;

                if (this.localCachedValues.Length == 0 && !this.generatorReportsFinished)
                {
                    // Fetch more values
                    try
                    {
                        EnumeratorResults<T> results = await this.owner.jsonRpc.InvokeWithCancellationAsync<EnumeratorResults<T>>(NextMethodName, this.nextOrDisposeArguments, this.cancellationToken).ConfigureAwait(false);
                        if (!results.Finished && results.Values?.Count == 0)
                        {
                            throw new UnexpectedEmptyEnumerableResponseException("The RPC server responded with an empty list of additional values for an incomplete list.");
                        }

                        if (results.Values is not null)
                        {
                            Write(this.localCachedValues, results.Values);
                        }

                        this.generatorReportsFinished = results.Finished;
                    }
                    catch (RemoteInvocationException ex)
                    {
                        // Avoid spending a message asking the server to dispose of the marshalled enumerator since they threw an exception at us.
                        this.generatorReportsFinished = true;

                        if (ex.ErrorCode == (int)JsonRpcErrorCode.NoMarshaledObjectFound)
                        {
                            throw new InvalidOperationException(ex.Message, ex);
                        }

                        throw;
                    }
                }

                return this.localCachedValues.Length > 0;
            }

            private static void Write(IBufferWriter<T> writer, IReadOnlyList<T> values)
            {
                Span<T> span = writer.GetSpan(values.Count);
                for (int i = 0; i < values.Count; i++)
                {
                    span[i] = values[i];
                }

                writer.Advance(values.Count);
            }
        }
    }
}
