// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using Microsoft;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Synchronizes activities as set by the <see cref="CorrelationManager"/> class over RPC.
    /// </summary>
    /// <seealso cref="ActivityTracingStrategy"/>
    public class CorrelationManagerTracingStrategy : IActivityTracingStrategy
    {
        private static readonly AsyncLocal<string?> TraceStateAsyncLocal = new AsyncLocal<string?>();

        /// <summary>
        /// Gets or sets the contextual <c>tracestate</c> value.
        /// </summary>
        public static string? TraceState
        {
            get => TraceStateAsyncLocal.Value;
            set => TraceStateAsyncLocal.Value = value;
        }

        /// <summary>
        /// Gets or sets the <see cref="System.Diagnostics.TraceSource"/> that will receive the activity transfer, start and stop events .
        /// </summary>
        public TraceSource? TraceSource { get; set; }

        /// <inheritdoc/>
        public unsafe void ApplyOutboundActivity(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));

            if (Trace.CorrelationManager.ActivityId != Guid.Empty)
            {
                var traceparent = default(TraceParent);

                FillRandomBytes(new Span<byte>(traceparent.ParentId, TraceParent.ParentIdByteCount));
                CopyGuidToBuffer(Trace.CorrelationManager.ActivityId, new Span<byte>(traceparent.TraceId, TraceParent.TraceIdByteCount));

                if (this.TraceSource is object && (this.TraceSource.Switch.Level & SourceLevels.ActivityTracing) == SourceLevels.ActivityTracing && this.TraceSource.Listeners.Count > 0)
                {
                    traceparent.Flags |= TraceParent.TraceFlags.Sampled;
                }

                request.TraceParent = traceparent.ToString();
                request.TraceState = TraceState;
            }
        }

        /// <inheritdoc/>
        public unsafe IDisposable? ApplyInboundActivity(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));

            var traceparent = new TraceParent(request.TraceParent);
            Guid childActivityId = Guid.NewGuid();
            string? activityName = this.GetInboundActivityName(request);

            return new ActivityState(request, this.TraceSource, activityName, traceparent.TraceIdGuid, childActivityId);
        }

        /// <summary>
        /// Determines the name to give to the activity started when dispatching an incoming RPC call.
        /// </summary>
        /// <param name="request">The inbound RPC request.</param>
        /// <returns>The name of the activity.</returns>
        /// <remarks>
        /// The default implementation uses <see cref="JsonRpcRequest.Method"/> as the activity name.
        /// </remarks>
        protected virtual string? GetInboundActivityName(JsonRpcRequest request) => request?.Method;

        private static unsafe void FillRandomBytes(Span<byte> buffer) => CopyGuidToBuffer(Guid.NewGuid(), buffer);

        private static unsafe void CopyGuidToBuffer(Guid guid, Span<byte> buffer)
        {
            Assumes.True(buffer.Length <= sizeof(Guid));
            ReadOnlySpan<byte> guidBytes = new ReadOnlySpan<byte>(&guid, sizeof(Guid));
            guidBytes.Slice(0, buffer.Length).CopyTo(buffer);
        }

        private class ActivityState : IDisposable
        {
            private readonly TraceSource? traceSource;
            private readonly Guid originalActivityId;
            private readonly string? originalTraceState;
            private readonly string? activityName;
            private readonly Guid parentTraceId;

            internal ActivityState(JsonRpcRequest request, TraceSource? traceSource, string? activityName, Guid parentTraceId, Guid childTraceId)
            {
                this.originalActivityId = Trace.CorrelationManager.ActivityId;
                this.originalTraceState = TraceState;
                this.activityName = activityName;
                this.parentTraceId = parentTraceId;

                if (traceSource is object && parentTraceId != Guid.Empty)
                {
                    // We set ActivityId to a short-lived value here for the sake of the TraceTransfer call that comes next.
                    // TraceTransfer goes from the current activity to the one passed as an argument.
                    // Without a traceSource object, there's no transfer and thus no need to set this temporary ActivityId.
                    Trace.CorrelationManager.ActivityId = parentTraceId;
                    traceSource.TraceTransfer(0, nameof(TraceEventType.Transfer), childTraceId);
                }

                Trace.CorrelationManager.ActivityId = childTraceId;
                TraceState = request.TraceState;

                traceSource?.TraceEvent(TraceEventType.Start, 0, this.activityName);

                this.traceSource = traceSource;
            }

            public void Dispose()
            {
                this.traceSource?.TraceEvent(TraceEventType.Stop, 0, this.activityName);

                if (this.parentTraceId != Guid.Empty)
                {
                    this.traceSource?.TraceTransfer(0, nameof(TraceEventType.Transfer), this.parentTraceId);
                }

                Trace.CorrelationManager.ActivityId = this.originalActivityId;
                TraceState = this.originalTraceState;
            }
        }
    }
}
