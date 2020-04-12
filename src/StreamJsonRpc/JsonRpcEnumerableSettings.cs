// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Collections.Generic;
    using Microsoft;

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
        /// Backing field for the <see cref="MinBatchSize"/> property.
        /// </summary>
        private int minBatchSize = 1;

        /// <summary>
        /// Backing field for the <see cref="MaxReadAhead"/> property.
        /// </summary>
        private int maxReadAhead;

        /// <summary>
        /// Gets or sets the maximum number of elements to read ahead and cache from the generator in anticipation of the consumer requesting those values.
        /// </summary>
        /// <value>This value must be a non-negative number.</value>
        public int MaxReadAhead
        {
            get => this.maxReadAhead;
            set
            {
                Requires.Range(value >= 0, nameof(value), Resources.NonNegativeIntegerRequired);
                this.maxReadAhead = value;
            }
        }

        /// <summary>
        /// Gets or sets the minimum number of elements to obtain from the generator before sending a batch of values to the consumer.
        /// </summary>
        /// <value>This must be a positive integer.</value>
        public int MinBatchSize
        {
            get => this.minBatchSize;
            set
            {
                Requires.Range(value > 0, nameof(value), Resources.PositiveIntegerRequired);
                this.minBatchSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the number of elements that should be precomputed and provided in the initial JSON-RPC message
        /// so the receiving party does not neet to request the initial few elements.
        /// </summary>
        /// <remarks>
        /// <para>This should only be used for <see cref="IAsyncEnumerable{T}"/> objects returned directly from an RPC method.</para>
        /// <para>To prefetch items for <see cref="IAsyncEnumerable{T}"/> objects used as arguments to an RPC method
        /// or within an object graph of a returned value, use the <see cref="JsonRpcExtensions.WithPrefetchAsync{T}(IAsyncEnumerable{T}, int, System.Threading.CancellationToken)"/> extension method
        /// instead and leave this value at 0.</para>
        /// </remarks>
        public int Prefetch { get; set; }
    }
}
