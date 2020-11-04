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

            if (request.TraceParent is null)
            {
                return null;
            }

            var oldState = new ActivityState(request, this.TraceSource);
            var traceparent = new TraceParent(request.TraceParent);
            TraceState = request.TraceState;

            if (oldState.ActivityId != Guid.Empty)
            {
                this.TraceSource?.TraceTransfer(0, nameof(TraceEventType.Transfer), traceparent.TraceIdGuid);
            }

            Trace.CorrelationManager.ActivityId = traceparent.TraceIdGuid;
            this.TraceSource?.TraceEvent(TraceEventType.Start, 0, request.Method);

            return oldState;
        }

        private static unsafe void FillRandomBytes(Span<byte> buffer) => CopyGuidToBuffer(Guid.NewGuid(), buffer);

        private static unsafe void CopyGuidToBuffer(Guid guid, Span<byte> buffer)
        {
            Assumes.True(buffer.Length <= sizeof(Guid));
            ReadOnlySpan<byte> guidBytes = new ReadOnlySpan<byte>(&guid, sizeof(Guid));
            guidBytes.Slice(0, buffer.Length).CopyTo(buffer);
        }

        private class ActivityState : IDisposable
        {
            private readonly JsonRpcRequest request;
            private readonly TraceSource? traceSource;

            internal ActivityState(JsonRpcRequest request, TraceSource? traceSource)
            {
                this.ActivityId = Trace.CorrelationManager.ActivityId;
                this.TraceState = CorrelationManagerTracingStrategy.TraceState;
                this.request = request;
                this.traceSource = traceSource;
            }

            internal Guid ActivityId { get; }

            internal string? TraceState { get; }

            public void Dispose()
            {
                this.traceSource?.TraceEvent(TraceEventType.Stop, 0, this.request.Method);
                if (this.ActivityId != Guid.Empty)
                {
                    this.traceSource?.TraceTransfer(0, nameof(TraceEventType.Transfer), this.ActivityId);
                }

                Trace.CorrelationManager.ActivityId = this.ActivityId;
                CorrelationManagerTracingStrategy.TraceState = this.TraceState;
            }
        }
    }
}
