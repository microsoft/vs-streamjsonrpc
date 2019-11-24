// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Nerdbank.Streams;

    /// <summary>
    /// A helper class that <see cref="IJsonRpcMessageFormatter"/> implementations may use to support <see cref="IAsyncEnumerable{T}"/> return values from RPC methods.
    /// </summary>
    public class MessageFormatterEnumerableTracker
    {
        private const string DisposeMethodName = "$/enumerator/dispose";
        private const string NextMethodName = "$/enumerator/next";

        private static readonly MethodInfo GetTokenOpenGenericMethod = typeof(MessageFormatterEnumerableTracker).GetRuntimeMethods().First(m => m.Name == nameof(GetToken) && m.IsGenericMethod);
        private static readonly MethodInfo CreateEnumerableProxyOpenGenericMethod = typeof(MessageFormatterEnumerableTracker).GetRuntimeMethods().First(m => m.Name == nameof(CreateEnumerableProxy) && m.IsGenericMethod);

        private readonly Dictionary<long, IGeneratingEnumeratorTracker> generatorsByToken = new Dictionary<long, IGeneratingEnumeratorTracker>();
        private readonly JsonRpc jsonRpc;

        private readonly object syncObject = new object();

        private long nextToken;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageFormatterEnumerableTracker"/> class.
        /// </summary>
        /// <param name="jsonRpc">The <see cref="JsonRpc"/> instance that may be used to send or receive RPC messages related to <see cref="IAsyncEnumerable{T}"/>.</param>
        public MessageFormatterEnumerableTracker(JsonRpc jsonRpc)
        {
            this.jsonRpc = jsonRpc ?? throw new ArgumentNullException(nameof(jsonRpc));

            this.jsonRpc.AddLocalRpcMethod(NextMethodName, new Func<long, CancellationToken, ValueTask<object>>(this.OnNextAsync));
            this.jsonRpc.AddLocalRpcMethod(DisposeMethodName, new Func<long, ValueTask>(this.OnDisposeAsync));
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
        public static bool CanSerialize(Type objectType) => TrackerHelpers<IAsyncEnumerable<int>>.CanSerialize(objectType);

        /// <summary>
        /// Checks if a given <see cref="Type"/> is exactly some closed generic type based on <see cref="IAsyncEnumerable{T}"/>.
        /// </summary>
        /// <param name="objectType">The type which may be <see cref="IAsyncEnumerable{T}"/>.</param>
        /// <returns>true if given <see cref="Type"/> is <see cref="IAsyncEnumerable{T}"/>; otherwise, false.</returns>
        public static bool CanDeserialize(Type objectType) => TrackerHelpers<IAsyncEnumerable<int>>.CanDeserialize(objectType);

        /// <summary>
        /// Used by the generator to assign a handle to the given <see cref="IAsyncEnumerable{T}"/>.
        /// </summary>
        /// <typeparam name="T">The type of value that is produced by the enumerable.</typeparam>
        /// <param name="enumerable">The enumerable to assign a handle to.</param>
        /// <returns>The handle that was assigned.</returns>
        public long GetToken<T>(IAsyncEnumerable<T> enumerable)
        {
            long handle = Interlocked.Increment(ref this.nextToken);
            lock (this.syncObject)
            {
                this.generatorsByToken.Add(handle, new GeneratingEnumeratorTracker<T>(enumerable, settings: enumerable.GetJsonRpcSettings()));
            }

            return handle;
        }

        /// <summary>
        /// Used by the generator to assign a handle to the given <see cref="IAsyncEnumerable{T}"/>.
        /// </summary>
        /// <param name="enumerable">The enumerable to assign a handle to.</param>
        /// <returns>The handle that was assigned.</returns>
        public long GetToken(object enumerable)
        {
            Requires.NotNull(enumerable, nameof(enumerable));

            Type? iface = TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(enumerable.GetType());
            Requires.Argument(iface != null, nameof(enumerable), message: null);

            MethodInfo closedGenericMethod = GetTokenOpenGenericMethod.MakeGenericMethod(iface.GenericTypeArguments[0]);
            return (long)closedGenericMethod.Invoke(this, new object[] { enumerable });
        }

        /// <summary>
        /// Used by the consumer to construct a proxy that implements <see cref="IAsyncEnumerable{T}"/>
        /// and gets all its values from a remote generator.
        /// </summary>
        /// <typeparam name="T">The type of value that is produced by the enumerable.</typeparam>
        /// <param name="handle">The handle specified by the generator that is used to obtain more values or dispose of the enumerator.</param>
        /// <returns>The enumerator.</returns>
        public IAsyncEnumerable<T> CreateEnumerableProxy<T>(object handle)
        {
            return new AsyncEnumerableProxy<T>(this.jsonRpc, handle);
        }

        /// <summary>
        /// Used by the consumer to construct a proxy that implements <see cref="IAsyncEnumerable{T}"/>
        /// and gets all its values from a remote generator.
        /// </summary>
        /// <param name="enumeratedType">The type of value that is produced by the enumerable.</param>
        /// <param name="handle">The handle specified by the generator that is used to obtain more values or dispose of the enumerator.</param>
        /// <returns>The enumerator.</returns>
        public object CreateEnumerableProxy(Type enumeratedType, object handle)
        {
            Requires.NotNull(enumeratedType, nameof(enumeratedType));
            Requires.NotNull(handle, nameof(handle));

            Requires.Argument(CanDeserialize(enumeratedType), nameof(enumeratedType), message: null);
            MethodInfo closedGenericMethod = CreateEnumerableProxyOpenGenericMethod.MakeGenericMethod(enumeratedType.GenericTypeArguments[0]);
            return closedGenericMethod.Invoke(this, new object[] { handle });
        }

        private ValueTask<object> OnNextAsync(long token, CancellationToken cancellationToken)
        {
            IGeneratingEnumeratorTracker generator;
            lock (this.syncObject)
            {
                generator = this.generatorsByToken[token];
            }

            return generator.GetNextValuesAsync(cancellationToken);
        }

        private ValueTask OnDisposeAsync(long token)
        {
            IGeneratingEnumeratorTracker generator;
            lock (this.syncObject)
            {
                generator = this.generatorsByToken[token];
                this.generatorsByToken.Remove(token);
            }

            return generator.DisposeAsync();
        }

        private class GeneratingEnumeratorTracker<T> : IGeneratingEnumeratorTracker
        {
            private readonly IAsyncEnumerator<T> enumerator;

            private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

            private readonly BufferBlock<T>? prefetchedElements;

            internal GeneratingEnumeratorTracker(IAsyncEnumerable<T> enumerable, JsonRpcEnumerableSettings settings)
            {
                this.enumerator = enumerable.GetAsyncEnumerator(this.cancellationTokenSource.Token);
                this.Settings = settings;

                if (settings.MaxReadAhead > 0)
                {
                    this.prefetchedElements = new BufferBlock<T>(new DataflowBlockOptions { BoundedCapacity = settings.MaxReadAhead, EnsureOrdered = true });
                    this.PrefetchAsync().Forget(); // exceptions fault the buffer block
                }
            }

            internal JsonRpcEnumerableSettings Settings { get; }

            public async ValueTask<object> GetNextValuesAsync(CancellationToken cancellationToken)
            {
                using (cancellationToken.Register(state => ((CancellationTokenSource)state).Cancel(), this.cancellationTokenSource))
                {
                    cancellationToken = this.cancellationTokenSource.Token;
                    bool finished = false;
                    var results = new List<T>(this.Settings.MinBatchSize);
                    if (this.prefetchedElements != null)
                    {
                        // Fetch at least the min batch size and at most the number that has been cached up to this point (or until we hit the end of the sequence).
                        // We snap the number of cached elements up front because as we dequeue, we create capacity to store more and we don't want to
                        // collect and return more than MaxReadAhead.
                        int cachedOnEntry = this.prefetchedElements.Count;
                        for (int i = 0; !this.prefetchedElements.Completion.IsCompleted && (i < this.Settings.MinBatchSize || (cachedOnEntry - results.Count > 0)); i++)
                        {
                            try
                            {
                                T element = await this.prefetchedElements.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                                results.Add(element);
                            }
                            catch (InvalidOperationException) when (this.prefetchedElements.Completion.IsCompleted)
                            {
                                // Race condition. The sequence is over.
                                finished = true;
                                break;
                            }
                        }

                        if (this.prefetchedElements.Completion.IsCompleted)
                        {
                            // Rethrow any exceptions.
                            await this.prefetchedElements.Completion.ConfigureAwait(false);
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

                    return new EnumeratorResults<T>
                    {
                        Finished = finished,
                        Values = results,
                    };
                }
            }

            public ValueTask DisposeAsync()
            {
                this.cancellationTokenSource.Cancel();
                this.prefetchedElements?.Complete();
                return this.enumerator.DisposeAsync();
            }

            private async Task PrefetchAsync()
            {
                Assumes.NotNull(this.prefetchedElements);
                try
                {
                    while (await this.enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        await this.prefetchedElements.SendAsync(this.enumerator.Current, this.cancellationTokenSource.Token).ConfigureAwait(false);
                    }

                    this.prefetchedElements.Complete();
                }
                catch (Exception ex)
                {
                    ITargetBlock<T> target = this.prefetchedElements;
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
            private object? handle;

            internal AsyncEnumerableProxy(JsonRpc jsonRpc, object handle)
            {
                this.jsonRpc = jsonRpc;
                this.handle = handle;
            }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken)
            {
                object handle = this.handle ?? throw new InvalidOperationException(Resources.UsableOnceOnly);
                this.handle = null;
                return new AsyncEnumeratorProxy<T>(this.jsonRpc, handle, cancellationToken);
            }
        }

        /// <summary>
        /// Provides the <see cref="IAsyncEnumerator{T}"/> instance that is used by a consumer.
        /// </summary>
        /// <typeparam name="T">The type of value produced by the enumerator.</typeparam>
        private class AsyncEnumeratorProxy<T> : IAsyncEnumerator<T>
        {
            private readonly JsonRpc jsonRpc;
            private readonly CancellationToken cancellationToken;
            private readonly object?[] nextOrDisposeArguments;

            /// <summary>
            /// A sequence of values that have already been received from the generator but not yet consumed.
            /// </summary>
            private Sequence<T> localCachedValues = new Sequence<T>();

            /// <summary>
            /// A value indicating whether the generator has reported that no more values will be forthcoming.
            /// </summary>
            private bool generatorReportsFinished;

            private bool disposed;

            internal AsyncEnumeratorProxy(JsonRpc jsonRpc, object handle, CancellationToken cancellationToken)
            {
                this.jsonRpc = jsonRpc;
                this.nextOrDisposeArguments = new object?[] { handle };
                this.cancellationToken = cancellationToken;
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

                    // Notify server.
                    await this.jsonRpc.NotifyAsync(DisposeMethodName, this.nextOrDisposeArguments).ConfigureAwait(false);
                }
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                Verify.NotDisposed(!this.disposed, this);

                // Consume one locally cached value, if we have one.
                if (this.localCachedValues.Length > 0)
                {
                    this.localCachedValues.AdvanceTo(this.localCachedValues.AsReadOnlySequence.GetPosition(1));
                }

                if (this.localCachedValues.Length == 0 && !this.generatorReportsFinished)
                {
                    // Fetch more values
                    EnumeratorResults<T> results = await this.jsonRpc.InvokeWithCancellationAsync<EnumeratorResults<T>>(NextMethodName, this.nextOrDisposeArguments, this.cancellationToken).ConfigureAwait(false);
                    if (results.Values != null)
                    {
                        Write(this.localCachedValues, results.Values);
                    }

                    this.generatorReportsFinished = results.Finished;
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

        [DataContract]
        private class EnumeratorResults<T>
        {
            [DataMember(Name = "values", Order = 0)]
            internal IReadOnlyList<T>? Values { get; set; }

            [DataMember(Name = "finished", Order = 1)]
            internal bool Finished { get; set; }
        }
    }
}
