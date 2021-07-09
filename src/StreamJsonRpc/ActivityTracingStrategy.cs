// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Diagnostics;
    using Microsoft;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Synchronizes activities as set by the <see cref="Activity"/> class over RPC.
    /// </summary>
    /// <seealso cref="CorrelationManagerTracingStrategy"/>
    public class ActivityTracingStrategy : IActivityTracingStrategy
    {
        /// <inheritdoc/>
        public void ApplyOutboundActivity(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));

            if (Activity.Current?.IdFormat == ActivityIdFormat.W3C)
            {
                request.TraceParent = Activity.Current.Id;
                request.TraceState = Activity.Current.TraceStateString;
            }
        }

        /// <inheritdoc/>
        public IDisposable? ApplyInboundActivity(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));

            var state = new State(new Activity(request.Method!));
            state.NewActivity.TraceStateString = request.TraceState;
            if (request.TraceParent is object)
            {
                state.NewActivity.SetParentId(request.TraceParent);
            }

            state.NewActivity.Start();
            return state;
        }

        private class State : IDisposable
        {
            internal State(Activity newActivity)
            {
                this.PriorActivity = Activity.Current;
                this.NewActivity = newActivity;
            }

            internal Activity? PriorActivity { get; }

            internal Activity NewActivity { get; }

            public void Dispose()
            {
                this.NewActivity.Stop();

                // Restore the original activity. This normally happens automatically,
                // but since we called SetParentId on the activity we created, the parent relationship is destroyed.
                if (this.PriorActivity is object && Activity.Current is null)
                {
                    Activity.Current = this.PriorActivity;
                }
            }
        }
    }
}
