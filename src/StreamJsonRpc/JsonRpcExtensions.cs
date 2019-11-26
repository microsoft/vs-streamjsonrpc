// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Collections.Generic;
    using System.Threading;
    using Microsoft;

    /// <summary>
    /// Extension methods for use with <see cref="JsonRpc"/>.
    /// </summary>
    public static class JsonRpcExtensions
    {
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

            return settings != null ? new SettingsBearingEnumerable<T>(enumerable, settings) : enumerable;
        }

        /// <summary>
        /// Converts an <see cref="IEnumerable{T}"/> to <see cref="IAsyncEnumerable{T}"/> so it will be streamed over an RPC connection progressively
        /// instead of as an entire collection in one message.
        /// </summary>
        /// <typeparam name="T">The type of element enumerated by the sequence.</typeparam>
        /// <param name="enumerable">The enumerable to be converted.</param>
        /// <returns>The async enumerable instance.</returns>
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            foreach (T item in enumerable)
            {
                yield return item;
            }
        }

        /// <summary>
        /// Converts an <see cref="IEnumerable{T}"/> to <see cref="IAsyncEnumerable{T}"/> so it will be streamed over an RPC connection progressively
        /// instead of as an entire collection in one message.
        /// </summary>
        /// <typeparam name="T">The type of element enumerated by the sequence.</typeparam>
        /// <param name="enumerable">The enumerable to be converted.</param>
        /// <param name="settings">The settings to associate with this enumerable.</param>
        /// <returns>The async enumerable instance.</returns>
        public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IEnumerable<T> enumerable, JsonRpcEnumerableSettings? settings)
        {
            return AsAsyncEnumerable(enumerable).WithJsonRpcSettings(settings);
        }

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

            return enumerable is SettingsBearingEnumerable<T> decorated ? decorated.Settings : JsonRpcEnumerableSettings.DefaultSettings;
        }

        internal class SettingsBearingEnumerable<T> : IAsyncEnumerable<T>
        {
            private readonly IAsyncEnumerable<T> innerEnumerable;

            internal SettingsBearingEnumerable(IAsyncEnumerable<T> enumerable, JsonRpcEnumerableSettings settings)
            {
                this.innerEnumerable = enumerable;
                this.Settings = settings;
            }

            internal JsonRpcEnumerableSettings Settings { get; }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken) => this.innerEnumerable.GetAsyncEnumerator(cancellationToken);
        }
    }
}
