// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Microsoft;

    /// <summary>
    /// Options that may customize how a dynamically generated client proxy object calls into a <see cref="JsonRpc"/> instance.
    /// </summary>
    public class JsonRpcProxyOptions
    {
        /// <summary>
        /// Backing field for the <see cref="MethodNameTransform"/> property.
        /// </summary>
        private Func<string, string> methodNameTransform = n => n;

        /// <summary>
        /// Gets or sets a function that takes the CLR method name and returns the RPC method name.
        /// This method is useful for adding prefixes to all methods, or making them camelCased.
        /// </summary>
        /// <value>A function, defaulting to a straight pass-through. Never null.</value>
        /// <exception cref="ArgumentNullException">Thrown if set to a null value.</exception>
        public Func<string, string> MethodNameTransform
        {
            get => this.methodNameTransform;
            set => this.methodNameTransform = Requires.NotNull(value, nameof(value));
        }

        /// <summary>
        /// Gets an instance with default properties.
        /// </summary>
        /// <remarks>
        /// Callers should *not* mutate properties on this instance since it is shared.
        /// </remarks>
        internal static JsonRpcProxyOptions Default { get; } = new JsonRpcProxyOptions();
    }
}
