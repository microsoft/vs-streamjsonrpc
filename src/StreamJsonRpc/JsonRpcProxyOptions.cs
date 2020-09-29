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
        /// Backing field for the <see cref="EventNameTransform"/> property.
        /// </summary>
        private Func<string, string> eventNameTransform = n => n;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcProxyOptions"/> class.
        /// </summary>
        public JsonRpcProxyOptions()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcProxyOptions"/> class
        /// with properties initialized from another instance.
        /// </summary>
        /// <param name="copyFrom">The options to copy values from.</param>
        public JsonRpcProxyOptions(JsonRpcProxyOptions copyFrom)
        {
            Requires.NotNull(copyFrom, nameof(copyFrom));

            this.MethodNameTransform = copyFrom.MethodNameTransform;
            this.EventNameTransform = copyFrom.EventNameTransform;
            this.ServerRequiresNamedArguments = copyFrom.ServerRequiresNamedArguments;
            this.OnDispose = copyFrom.OnDispose;
            this.OnProxyConstructed = copyFrom.OnProxyConstructed;
        }

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
        /// Gets or sets a function that takes the CLR event name and returns the RPC event name used in notifications.
        /// This method is useful for adding prefixes to all events, or making them camelCased.
        /// </summary>
        /// <value>A function, defaulting to a straight pass-through. Never null.</value>
        /// <exception cref="ArgumentNullException">Thrown if set to a null value.</exception>
        public Func<string, string> EventNameTransform
        {
            get => this.eventNameTransform;
            set => this.eventNameTransform = Requires.NotNull(value, nameof(value));
        }

        /// <summary>
        /// Gets or sets a value indicating whether the client proxy will pass named arguments (i.e. an args object)
        /// instead of the default positional arguments (i.e. an args array).
        /// </summary>
        public bool ServerRequiresNamedArguments { get; set; }

        /// <summary>
        /// Gets an instance with default properties.
        /// </summary>
        /// <remarks>
        /// Callers should *not* mutate properties on this instance since it is shared.
        /// </remarks>
        internal static JsonRpcProxyOptions Default { get; } = new JsonRpcProxyOptions();

        /// <summary>
        /// Gets or sets a delegate to invoke when this proxy is disposed.
        /// </summary>
        /// <remarks>
        /// When set, the proxy will *not* automatically dispose of the owning <see cref="JsonRpc"/> instance.
        /// This delegate *may* be called concurrently or more than once if the proxy owner calls <see cref="IDisposable.Dispose"/> concurrently.
        /// </remarks>
        internal Action? OnDispose { get; set; }

        /// <summary>
        /// Gets or sets a callback that is invoked whenever a proxy is constructed.
        /// </summary>
        internal Action<IJsonRpcClientProxyInternal>? OnProxyConstructed { get; set; }
    }
}
