// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using StreamJsonRpc.Protocol;

    internal class StandardCancellationStrategy : ICancellationStrategy
    {
        /// <summary>
        /// The JSON-RPC method name used to send/receive cancellation requests.
        /// </summary>
        private const string CancelRequestSpecialMethod = "$/cancelRequest";

        /// <summary>
        /// The <see cref="MethodInfo"/> for the <see cref="CancelInboundRequest(RequestId)"/> method.
        /// </summary>
        private static readonly MethodInfo CancelInboundRequestMethodInfo = typeof(StandardCancellationStrategy).GetMethod(nameof(CancelInboundRequest), BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// A map of id's from inbound calls that have not yet completed and may be canceled,
        /// to their <see cref="CancellationTokenSource"/> instances.
        /// </summary>
        private readonly Dictionary<RequestId, CancellationTokenSource> inboundCancellationSources = new Dictionary<RequestId, CancellationTokenSource>();

        /// <summary>
        /// Initializes a new instance of the <see cref="StandardCancellationStrategy"/> class.
        /// </summary>
        /// <param name="jsonRpc">The <see cref="JsonRpc"/> connection that this strategy is associated with.</param>
        public StandardCancellationStrategy(JsonRpc jsonRpc)
        {
            this.JsonRpc = jsonRpc ?? throw new ArgumentNullException(nameof(jsonRpc));

            // When we add this method, we *must* specify to use a plain SynchronizationContext instance
            // instead of whatever the user may be using in order to support cancelling of server methods
            // that have not yet yielded their SyncContext back for calling another method.
            this.JsonRpc.AddLocalRpcMethod(
                CancelInboundRequestMethodInfo,
                this,
                new JsonRpcMethodAttribute(CancelRequestSpecialMethod),
                JsonRpc.DefaultSynchronizationContext);
        }

        /// <summary>
        /// Gets the <see cref="JsonRpc"/> connection that this strategy is associated with.
        /// </summary>
        internal JsonRpc JsonRpc { get; }

        /// <inheritdoc />
        public virtual void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource)
        {
            lock (this.inboundCancellationSources)
            {
                this.inboundCancellationSources.Add(requestId, cancellationTokenSource);
            }
        }

        /// <inheritdoc />
        public virtual void IncomingRequestEnded(RequestId requestId)
        {
            lock (this.inboundCancellationSources)
            {
                this.inboundCancellationSources.Remove(requestId);
            }
        }

        /// <inheritdoc />
        public virtual void CancelOutboundRequest(RequestId requestId)
        {
            Task.Run(async delegate
            {
                if (!this.JsonRpc.IsDisposed)
                {
                    if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
                    {
                        JsonRpcEventSource.Instance.SendingCancellationRequest(requestId.NumberIfPossibleForEvent);
                    }

                    var args = new Dictionary<string, object?>
                    {
                        { "id", requestId },
                    };

                    await this.JsonRpc.NotifyWithParameterObjectAsync(CancelRequestSpecialMethod, args).ConfigureAwait(false);
                }
            }).Forget();
        }

        /// <summary>
        /// Cancels an inbound request that was previously received by <see cref="IncomingRequestStarted(RequestId, CancellationTokenSource)"/>.
        /// </summary>
        /// <param name="id">The ID of the request to be canceled.</param>
        /// <devremarks>
        /// The name of the only parameter MUST be "id" in order to match the named arguments in the JSON-RPC request.
        /// </devremarks>
        protected void CancelInboundRequest(RequestId id)
        {
            if (this.JsonRpc.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.JsonRpc.TraceSource.TraceEvent(TraceEventType.Information, (int)JsonRpc.TraceEvents.ReceivedCancellation, "Cancellation request received for \"{0}\".", id);
            }

            if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
            {
                JsonRpcEventSource.Instance.ReceivedCancellationRequest(id.NumberIfPossibleForEvent);
            }

            CancellationTokenSource? cts;
            lock (this.inboundCancellationSources)
            {
                if (this.inboundCancellationSources.TryGetValue(id, out cts))
                {
                    this.inboundCancellationSources.Remove(id);
                }
            }

            if (cts is object)
            {
                // This cancellation token is the one that is passed to the server method.
                // It may have callbacks registered on cancellation.
                // Cancel it asynchronously to ensure that these callbacks do not delay handling of other json rpc messages.
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // There is a race condition between when we retrieve the CTS and actually call Cancel,
                    // vs. another thread that disposes the CTS at the conclusion of the method invocation.
                    // It cannot be prevented, so just swallow it since the method executed successfully.
                }
            }
        }
    }
}
