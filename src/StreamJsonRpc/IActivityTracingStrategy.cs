// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Synchronizes contextual activities between an RPC client and server
    /// consistent with the <see href="https://www.w3.org/TR/trace-context/">W3C Trace Context</see> specification.
    /// </summary>
    /// <remarks>
    /// Implementations are required to be thread-safe.
    /// </remarks>
    public interface IActivityTracingStrategy
    {
        /// <summary>
        /// Applies a contextual activity to an outbound RPC request.
        /// </summary>
        /// <param name="request">The outbound RPC request.</param>
        /// <remarks>
        /// This method may be invoked regardless of whether a contextual activity exists.
        /// </remarks>
        void ApplyOutboundActivity(JsonRpcRequest request);

        /// <summary>
        /// Applies an activity described in an incoming RPC request to the current context so the dispatched method can inherit it.
        /// </summary>
        /// <param name="request">The inbound RPC request.</param>
        /// <returns>An optional disposable object that can revert the effects taken by this method at the conclusion of the dispatched RPC server method.</returns>
        /// <remarks>
        /// This method may be invoked regardless of whether an activity is identified in the incoming request.
        /// The <paramref name="request"/> may be referenced by the returned <see cref="IDisposable"/> value if necessary.
        /// </remarks>
        IDisposable? ApplyInboundActivity(JsonRpcRequest request);
    }
}
