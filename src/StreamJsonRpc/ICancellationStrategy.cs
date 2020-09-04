// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Threading;

    /// <summary>
    /// Defines an extensibility point by which RPC methods may be canceled using <see cref="CancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// <para>A cancellation strategy can be set on the <see cref="JsonRpc.CancellationStrategy"/> property.</para>
    /// <para>The default implementation is defined by <see cref="StandardCancellationStrategy"/>.</para>
    /// <para>Implementations must be thread-safe.</para>
    /// </remarks>
    public interface ICancellationStrategy
    {
        /// <summary>
        /// Translates a canceled <see cref="CancellationToken"/> that was used in an outbound RPC request into terms that
        /// the RPC server can understand.
        /// </summary>
        /// <param name="requestId">The ID of the canceled request.</param>
        /// <remarks>
        /// Every call to this method is followed by a subsequent call to <see cref="OutboundRequestEnded(RequestId)"/>.
        /// </remarks>
        void CancelOutboundRequest(RequestId requestId);

        /// <summary>
        /// Cleans up any state associated with an earlier <see cref="CancelOutboundRequest(RequestId)"/> call.
        /// </summary>
        /// <param name="requestId">The ID of the canceled request.</param>
        /// <remarks>
        /// This method is invoked by <see cref="JsonRpc"/> when the response to a canceled request has been received.
        /// It *may* be invoked for requests for which a prior call to <see cref="CancelOutboundRequest(RequestId)"/> was *not* made, due to timing.
        /// But it should never be invoked concurrently with <see cref="CancelOutboundRequest(RequestId)"/> for the same <see cref="RequestId"/>.
        /// </remarks>
        void OutboundRequestEnded(RequestId requestId);

        /// <summary>
        /// Associates the <see cref="RequestId"/> from an incoming request with the <see cref="CancellationTokenSource"/>
        /// that is used for the <see cref="CancellationToken"/> passed to that RPC method so it can be canceled later.
        /// </summary>
        /// <param name="requestId">The ID of the incoming request.</param>
        /// <param name="cancellationTokenSource">A means to cancel the <see cref="CancellationToken"/> that will be used when invoking the RPC server method.</param>
        /// <remarks>
        /// Implementations are expected to store the arguments in a dictionary so the implementing strategy can cancel it when the trigger occurs.
        /// The trigger is outside the scope of this interface and will vary by implementation.
        /// </remarks>
        void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource);

        /// <summary>
        /// Cleans up any state associated with an earlier <see cref="IncomingRequestStarted(RequestId, CancellationTokenSource)"/> call.
        /// </summary>
        /// <param name="requestId">The ID of the request that has been fulfilled.</param>
        /// <remarks>
        /// Implementations are expected to release memory allocated by a prior call to <see cref="IncomingRequestStarted(RequestId, CancellationTokenSource)"/>.
        /// This method should *not* dispose of the <see cref="CancellationTokenSource"/> received previously as <see cref="JsonRpc"/> owns its lifetime.
        /// </remarks>
        void IncomingRequestEnded(RequestId requestId);
    }
}
