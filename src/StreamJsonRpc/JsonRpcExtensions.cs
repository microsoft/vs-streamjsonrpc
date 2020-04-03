// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;

    /// <summary>
    /// Extension methods for use with <see cref="JsonRpc"/>.
    /// </summary>
    public static class JsonRpcExtensions
    {
        /// <summary>
        /// A non-generic interface implemented by <see cref="RpcEnumerable{T}"/>
        /// so that non-generic callers can initiate a prefetch if necessary.
        /// </summary>
        private interface IRpcEnumerable
        {
            JsonRpcEnumerableSettings Settings { get; }

            Task PrefetchAsync(int count, CancellationToken cancellationToken);
        }

#pragma warning disable VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
        /// <summary>
        /// Decorates an <see cref="IAsyncEnumerable{T}"/> with settings that customize how StreamJsonRpc will send its items to the remote party.
        /// </summary>
        /// <typeparam name="T">The type of element enumerated by the sequence.</typeparam>
        /// <param name="enumerable">The enumerable to be decorated.</param>
        /// <param name="settings">The settings to associate with this enumerable.</param>
        /// <returns>The decorated enumerable instance.</returns>
        public static IAsyncEnumerable<T> WithJsonRpcSettings<T>(this IAsyncEnumerable<T> enumerable, JsonRpcEnumerableSettings? settings)
        {
            Requires.NotNull(enumerable, nameof(enumerable));

            if (settings == null)
            {
                return enumerable;
            }

            RpcEnumerable<T> rpcEnumerable = GetRpcEnumerable(enumerable);
            rpcEnumerable.Settings = settings;
            return rpcEnumerable;
        }

        /// <inheritdoc cref="AsAsyncEnumerable{T}(IEnumerable{T}, CancellationToken)"/>
        public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable) => AsAsyncEnumerable<T>(enumerable, CancellationToken.None);

        /// <summary>
        /// Converts an <see cref="IEnumerable{T}"/> to <see cref="IAsyncEnumerable{T}"/> so it will be streamed over an RPC connection progressively
        /// instead of as an entire collection in one message.
        /// </summary>
        /// <typeparam name="T">The type of element enumerated by the sequence.</typeparam>
        /// <param name="enumerable">The enumerable to be converted.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The async enumerable instance.</returns>
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable, [EnumeratorCancellation] CancellationToken cancellationToken)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            Requires.NotNull(enumerable, nameof(enumerable));

            cancellationToken.ThrowIfCancellationRequested();
            foreach (T item in enumerable)
            {
                yield return item;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <inheritdoc cref="AsAsyncEnumerable{T}(IEnumerable{T}, JsonRpcEnumerableSettings?, CancellationToken)"/>
        public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable, JsonRpcEnumerableSettings? settings) => AsAsyncEnumerable(enumerable, settings, CancellationToken.None);

        /// <summary>
        /// Converts an <see cref="IEnumerable{T}"/> to <see cref="IAsyncEnumerable{T}"/> so it will be streamed over an RPC connection progressively
        /// instead of as an entire collection in one message.
        /// </summary>
        /// <typeparam name="T">The type of element enumerated by the sequence.</typeparam>
        /// <param name="enumerable">The enumerable to be converted.</param>
        /// <param name="settings">The settings to associate with this enumerable.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The async enumerable instance.</returns>
        public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable, JsonRpcEnumerableSettings? settings, CancellationToken cancellationToken)
        {
            return AsAsyncEnumerable(enumerable, cancellationToken).WithJsonRpcSettings(settings);
        }

        /// <summary>
        /// Preloads an <see cref="IAsyncEnumerable{T}"/> with a cache of pre-enumerated items for inclusion in the initial transmission
        /// of the enumerable over an RPC channel.
        /// </summary>
        /// <typeparam name="T">The type of item in the collection.</typeparam>
        /// <param name="enumerable">The sequence to pre-fetch items from.</param>
        /// <param name="count">The number of items to pre-fetch. If this value is larger than the number of elements in the enumerable, all values will be pre-fetched.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A decorated <see cref="IAsyncEnumerable{T}"/> object that is specially prepared for processing by JSON-RPC with the preloaded values.</returns>
        public static async ValueTask<IAsyncEnumerable<T>> WithPrefetchAsync<T>(this IAsyncEnumerable<T> enumerable, int count, CancellationToken cancellationToken = default)
        {
            Requires.NotNull(enumerable, nameof(enumerable));

            if (count == 0)
            {
                return enumerable;
            }

            RpcEnumerable<T> rpcEnumerable = GetRpcEnumerable(enumerable);
            await rpcEnumerable.PrefetchAsync(count, cancellationToken).ConfigureAwait(false);
            return rpcEnumerable;
        }
#pragma warning restore VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.

        /// <summary>
        /// Extracts the <see cref="JsonRpcEnumerableSettings"/> from an <see cref="IAsyncEnumerable{T}"/>
        /// that may have been previously returned from <see cref="WithJsonRpcSettings{T}(IAsyncEnumerable{T}, JsonRpcEnumerableSettings?)"/>.
        /// </summary>
        /// <typeparam name="T">The type of element enumerated by the sequence.</typeparam>
        /// <param name="enumerable">The enumerable, which may have come from <see cref="WithJsonRpcSettings{T}(IAsyncEnumerable{T}, JsonRpcEnumerableSettings?)"/>.</param>
        /// <returns>The settings to use.</returns>
        /// <remarks>
        /// If the <paramref name="enumerable"/> did not come from <see cref="WithJsonRpcSettings{T}(IAsyncEnumerable{T}, JsonRpcEnumerableSettings?)"/>,
        /// the default settings will be returned.
        /// </remarks>
        internal static JsonRpcEnumerableSettings GetJsonRpcSettings<T>(this IAsyncEnumerable<T> enumerable)
        {
            Requires.NotNull(enumerable, nameof(enumerable));

            return (enumerable as RpcEnumerable<T>)?.Settings ?? JsonRpcEnumerableSettings.DefaultSettings;
        }

        /// <summary>
        /// Executes the pre-fetch of values for an <see cref="IAsyncEnumerable{T}"/> object if
        /// prefetch is set with the <see cref="JsonRpcEnumerableSettings.Prefetch"/> property.
        /// </summary>
        /// <param name="possibleEnumerable">An object which might represent an <see cref="IAsyncEnumerable{T}"/>.</param>
        /// <param name="cancellationToken">A token to cancel prefetching.</param>
        /// <returns>A task that tracks completion of the prefetch operation.</returns>
        internal static Task PrefetchIfApplicableAsync(object? possibleEnumerable, CancellationToken cancellationToken)
        {
            if (possibleEnumerable is IRpcEnumerable enumerable)
            {
                if (enumerable.Settings?.Prefetch > 0)
                {
                    // TODO: What if the async iterator method throws? (think OCE or other)
                    return enumerable.PrefetchAsync(enumerable.Settings.Prefetch, cancellationToken);
                }
            }

            return Task.CompletedTask;
        }

        internal static (IReadOnlyList<T> Elements, bool Finished) TearOffPrefetchedElements<T>(this IAsyncEnumerable<T> enumerable)
        {
            Requires.NotNull(enumerable, nameof(enumerable));

            return (enumerable as RpcEnumerable<T>)?.TearOffPrefetchedElements() ?? (Array.Empty<T>(), false);
        }

        private static RpcEnumerable<T> GetRpcEnumerable<T>(IAsyncEnumerable<T> enumerable) => enumerable as RpcEnumerable<T> ?? new RpcEnumerable<T>(enumerable);

        private class RpcEnumerable<T> : IAsyncEnumerable<T>, IRpcEnumerable
        {
            private readonly RpcEnumerator innerEnumerator;

            private bool enumeratorRequested;

            internal RpcEnumerable(IAsyncEnumerable<T> enumerable)
            {
                this.innerEnumerator = new RpcEnumerator(enumerable);
            }

            public JsonRpcEnumerableSettings Settings { get; internal set; } = JsonRpcEnumerableSettings.DefaultSettings;

            public Task PrefetchAsync(int count, CancellationToken cancellationToken)
            {
                Verify.Operation(!this.enumeratorRequested, Resources.CannotBeCalledAfterGetAsyncEnumerator);
                return this.innerEnumerator.PrefetchAsync(count, cancellationToken);
            }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken)
            {
                Verify.Operation(!this.enumeratorRequested, Resources.CannotBeCalledAfterGetAsyncEnumerator);
                this.enumeratorRequested = true;
                return this.innerEnumerator;
            }

            internal (IReadOnlyList<T> Elements, bool Finished) TearOffPrefetchedElements() => this.innerEnumerator.TearOffPrefetchedElements();

            private class RpcEnumerator : IAsyncEnumerator<T>
            {
                private readonly IAsyncEnumerator<T> innerEnumerator;

                private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

                private List<T>? prefetchedElements;

                private bool finished;

                private bool moveNextHasBeenCalled;

                internal RpcEnumerator(IAsyncEnumerable<T> enumerable)
                {
                    this.innerEnumerator = enumerable.GetAsyncEnumerator(this.cancellationTokenSource.Token);
                }

                public T Current => this.moveNextHasBeenCalled && this.prefetchedElements?.Count > 0 ? this.prefetchedElements[0] : this.innerEnumerator.Current;

                public ValueTask DisposeAsync()
                {
                    return this.innerEnumerator.DisposeAsync();
                }

                public ValueTask<bool> MoveNextAsync()
                {
                    bool moveNextHasBeenCalledBefore = this.moveNextHasBeenCalled;
                    this.moveNextHasBeenCalled = true;

                    if (this.prefetchedElements?.Count > 0 && moveNextHasBeenCalledBefore)
                    {
                        this.prefetchedElements.RemoveAt(0);
                    }

                    if (this.prefetchedElements?.Count > 0)
                    {
                        return new ValueTask<bool>(true);
                    }

                    return this.innerEnumerator.MoveNextAsync();
                }

                internal (IReadOnlyList<T> Elements, bool Finished) TearOffPrefetchedElements()
                {
                    Assumes.False(this.moveNextHasBeenCalled);
                    IReadOnlyList<T> result = (IReadOnlyList<T>?)this.prefetchedElements ?? Array.Empty<T>();
                    this.prefetchedElements = null;
                    return (result, this.finished);
                }

                internal async Task PrefetchAsync(int count, CancellationToken cancellationToken)
                {
                    Requires.Range(count >= 0, nameof(count));
                    Verify.Operation(this.prefetchedElements == null, Resources.ElementsAlreadyPrefetched);

                    // Arrange to cancel the entire enumerator if the prefetch is canceled.
                    using CancellationTokenRegistration ctr = this.LinkToCancellation(cancellationToken);

                    var prefetchedElements = new List<T>(count);
                    bool moreAvailable = true;
                    for (int i = 0; i < count && (moreAvailable = await this.innerEnumerator.MoveNextAsync().ConfigureAwait(false)); i++)
                    {
                        prefetchedElements.Add(this.innerEnumerator.Current);
                    }

                    this.finished = !moreAvailable;
                    this.prefetchedElements = prefetchedElements;
                }

                private CancellationTokenRegistration LinkToCancellation(CancellationToken cancellationToken) => cancellationToken.Register(cts => ((CancellationTokenSource)cts).Cancel(), this.cancellationTokenSource);
            }
        }
    }
}
