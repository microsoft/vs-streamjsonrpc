// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <inheritdoc cref="IRpcMarshaledContext{T}"/>
internal class RpcMarshaledContext<T> : IRpcMarshaledContext<T>
    where T : class
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMarshaledContext{T}"/> class.
    /// </summary>
    /// <param name="value">The value that should be used in the object graph to be sent over RPC, to trigger marshaling.</param>
    /// <param name="options">The <see cref="JsonRpcTargetOptions"/> to use when adding this object as an RPC target.</param>
    internal RpcMarshaledContext(T value, JsonRpcTargetOptions options)
    {
        // We shouldn't reach this point with a proxy.
        Requires.Argument(value is not IJsonRpcClientProxyInternal, nameof(value), "Cannot marshal a proxy.");

        this.Proxy = value;
        this.JsonRpcTargetOptions = options;
    }

    /// <inheritdoc />
    public event EventHandler? Disposed;

    /// <inheritdoc />
    public T Proxy { get; private set; }

    /// <inheritdoc />
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public JsonRpcTargetOptions JsonRpcTargetOptions { get; }

    /// <summary>
    /// Immediately terminates the marshaling relationship.
    /// This releases resources allocated to facilitating the marshaling of the object
    /// and prevents any further invocations of the object by the remote party.
    /// If the underlying object implements <see cref="IDisposable"/> then its
    /// <see cref="IDisposable.Dispose()"/> method is also invoked.
    /// </summary>
    public void Dispose()
    {
        throw new NotImplementedException();
#pragma warning disable CS0162 // Unreachable code detected
        this.IsDisposed = true;
        this.Disposed?.Invoke(this, EventArgs.Empty);
#pragma warning restore CS0162 // Unreachable code detected
    }
}
