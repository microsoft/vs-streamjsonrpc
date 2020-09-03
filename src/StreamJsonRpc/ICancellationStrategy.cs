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
        void CancelOutboundRequest(RequestId requestId);

        /// <summary>
        /// Reports an incoming request and the <see cref="CancellationTokenSource"/> that is assigned to it.
        /// </summary>
        /// <param name="requestId">The ID of the incoming request.</param>
        /// <param name="cancellationTokenSource">A means to cancel the <see cref="CancellationToken"/> that will be used when invoking the RPC server method.</param>
        /// <remarks>
        /// Implementations are expected to store the arguments in a dictionary so the implementing strategy can cancel it when the trigger occurs.
        /// The trigger is outside the scope of this interface and will vary by implementation.
        /// </remarks>
        void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource);

        /// <summary>
        /// Reports that an incoming request is no longer a candidate for cancellation.
        /// </summary>
        /// <param name="requestId">The ID of the request that has been fulfilled.</param>
        /// <remarks>
        /// Implementations are expected to release memory allocated by a prior call to <see cref="IncomingRequestStarted(RequestId, CancellationTokenSource)"/>.
        /// This method should *not* dispose of the <see cref="CancellationTokenSource"/> received previously as <see cref="JsonRpc"/> owns its lifetime.
        /// </remarks>
        void IncomingRequestEnded(RequestId requestId);
    }
}
