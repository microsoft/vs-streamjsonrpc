// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Represents an object to be marshaled and provides check or influence its lifetime.
/// </summary>
/// <remarks>
/// When an object implementing this interface is disposed
/// the marshaling relationship is immediately terminated.
/// This releases resources allocated to facilitating the marshaling of the object
/// and prevents any further invocations of the object by the remote party.
/// If the underlying object implements <see cref="IDisposable"/> then its
/// <see cref="IDisposable.Dispose()"/> method is also invoked.
/// </remarks>
/// <devremarks>
/// This type is an interface rather than a class so that users can enjoy covariance on the generic type parameter.
/// </devremarks>
internal class RpcMarshaledContext : IDisposableObservable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMarshaledContext"/> class.
    /// </summary>
    /// <param name="interfaceType">The declared type of the proxy.</param>
    /// <param name="marshaledObject">The value that should be used in the object graph to be sent over RPC, to trigger marshaling.</param>
    /// <param name="options">The <see cref="JsonRpcTargetOptions"/> to use when adding this object as an RPC target.</param>
    internal RpcMarshaledContext(Type interfaceType, object marshaledObject, JsonRpcTargetOptions options)
    {
        // We shouldn't reach this point with a proxy.
        Requires.Argument(marshaledObject is not IJsonRpcClientProxyInternal, nameof(marshaledObject), "Cannot marshal a proxy.");

        this.DeclaredType = interfaceType;
        this.Proxy = marshaledObject;
        this.JsonRpcTargetOptions = options;
    }

    /// <summary>
    /// Occurs when the marshalling relationship is released,
    /// whether by a local call to <see cref="IDisposable.Dispose()" />
    /// or at the remote party's request.
    /// </summary>
    public event EventHandler? Disposed;

    /// <summary>
    /// Gets the interface type whose methods are exposed for RPC invocation on the target object.
    /// </summary>
    public Type DeclaredType { get; }

    /// <summary>
    /// Gets the marshalable proxy that should be used in the RPC message.
    /// </summary>
    public object Proxy { get; }

    /// <inheritdoc />
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Gets the <see cref="JsonRpcTargetOptions"/> to associate with this object when it becomes a RPC target.
    /// </summary>
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
