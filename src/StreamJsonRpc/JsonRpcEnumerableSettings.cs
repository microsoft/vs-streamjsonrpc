// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Collections.Generic;

    /// <summary>
    /// Provides customizations on performance characteristics of an <see cref="IAsyncEnumerable{T}"/> that is passed over JSON-RPC.
    /// </summary>
    public class JsonRpcEnumerableSettings
    {
        /// <summary>
        /// A shared instance with the default settings to use.
        /// </summary>
        /// <devremarks>
        /// This is internal because the type is mutable and thus cannot be safely shared.
        /// </devremarks>
        internal static readonly JsonRpcEnumerableSettings DefaultSettings = new JsonRpcEnumerableSettings();

        /// <summary>
        /// Gets or sets the maximum number of elements to read ahead and cache from the generator in anticipation of the consumer requesting those values.
        /// </summary>
        public int MaxReadAhead { get; set; }

        /// <summary>
        /// Gets or sets the minimum number of elements to obtain from the generator before sending a batch of values to the consumer.
        /// </summary>
        public int MinBatchSize { get; set; } = 1;
    }
}
