// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Microsoft;

    /// <summary>
    /// Represents an object to be marshaled and provides check or influence its lifetime.
    /// </summary>
    /// <typeparam name="T">The interface type whose methods are exposed for RPC invocation on the target object.</typeparam>
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
    internal interface IRpcMarshaledContext<out T> : IDisposableObservable
        where T : class
    {
        /// <summary>
        /// Occurs when the marshalling relationship is released,
        /// whether by a local call to <see cref="IDisposable.Dispose()" />
        /// or at the remote party's request.
        /// </summary>
        event EventHandler? Disposed;

        /// <summary>
        /// Gets the marshalable proxy that should be used in the RPC message.
        /// </summary>
        T Proxy { get; }

        /// <summary>
        /// Gets the <see cref="JsonRpcTargetOptions"/> to associate with this object when it becomes a RPC target.
        /// </summary>
        JsonRpcTargetOptions JsonRpcTargetOptions { get; }
    }
}
