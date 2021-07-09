// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class CorrelationManagerTracingStrategyTests : TestBase
{
    private const string SampleTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    private const string SampleTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private static readonly Guid SampleParentId = new Guid("352ff94b-b377-a64d-a3ce-929d0e0e4736");
    private readonly CorrelationManagerTracingStrategy strategy = new CorrelationManagerTracingStrategy();
    private readonly JsonRpcRequest request = new JsonRpcRequest { Method = "test" };

    public CorrelationManagerTracingStrategyTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void Outbound_NoContextualActivity()
    {
        this.strategy.ApplyOutboundActivity(this.request);
        Assert.Null(this.request.TraceParent);
        Assert.Null(this.request.TraceState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k=v")]
    [InlineData("k=v,k2=v2")]
    public void Outbound_WithContextualActivity(string? traceState)
    {
        Trace.CorrelationManager.ActivityId = SampleParentId;
        CorrelationManagerTracingStrategy.TraceState = traceState;
        try
        {
            this.strategy.ApplyOutboundActivity(this.request);
            Assert.Contains(SampleTraceId, this.request.TraceParent);
            Assert.Equal(traceState, this.request.TraceState);
        }
        finally
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
        }
    }

    [Theory]
    [CombinatorialData]
    public void Outbound_SampleFlag(bool sampled)
    {
        if (sampled)
        {
            this.strategy.TraceSource = new TraceSource("test", SourceLevels.ActivityTracing);
        }

        Trace.CorrelationManager.ActivityId = SampleParentId;
        try
        {
            this.strategy.ApplyOutboundActivity(this.request);
            Assert.EndsWith(sampled ? "-01" : "-00", this.request.TraceParent);
        }
        finally
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
        }
    }

    [Theory]
    [CombinatorialData]
    public void Inbound_WithoutTraceParent(bool contextualActivity)
    {
        var listener = new CollectingTraceListener();
        this.strategy.TraceSource = new TraceSource("test", SourceLevels.ActivityTracing)
        {
            Listeners = { listener },
        };

        Guid testActivityId = contextualActivity ? Guid.NewGuid() : Guid.Empty;
        Trace.CorrelationManager.ActivityId = testActivityId;
        try
        {
            using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
            {
                Assert.NotEqual(testActivityId, Trace.CorrelationManager.ActivityId);
            }

            // No transfers should have been recorded since there was no parent activity.
            Assert.Empty(listener.Transfers);

            Assert.Equal(testActivityId, Trace.CorrelationManager.ActivityId);
        }
        finally
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("k=v")]
    public void Inbound_TraceState(string? traceState)
    {
        try
        {
            this.request.TraceParent = SampleTraceParent;
            this.request.TraceState = traceState;

            using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
            {
                Assert.Equal(traceState, CorrelationManagerTracingStrategy.TraceState);
            }
        }
        finally
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
        }
    }

    [Theory, CombinatorialData]
    public void Inbound_Activity(bool predefinedActivity, bool upperCaseTraceParent)
    {
        var listener = new CollectingTraceListener();
        this.strategy.TraceSource = new TraceSource("test", SourceLevels.ActivityTracing)
        {
            Listeners = { listener },
        };

        Guid testContext = Guid.NewGuid();
        if (predefinedActivity)
        {
            Trace.CorrelationManager.ActivityId = testContext;
        }

        this.request.TraceParent = upperCaseTraceParent ? SampleTraceParent.ToUpperInvariant() : SampleTraceParent;
        (Guid RelatedActivityId, Guid CurrentActivityId) transfer1;
        using (this.strategy.ApplyInboundActivity(this.request))
        {
            transfer1 = Assert.Single(listener.Transfers);
            Assert.Equal(SampleParentId, transfer1.CurrentActivityId);
            Assert.NotEqual(SampleParentId, transfer1.RelatedActivityId);
            Assert.NotEqual(testContext, transfer1.RelatedActivityId);

            Assert.Equal(transfer1.RelatedActivityId, Trace.CorrelationManager.ActivityId);

            var start = listener.Events.Single(e => e.EventType == TraceEventType.Start);
            Assert.Equal(this.request.Method, start.Message);
        }

        Assert.Equal(predefinedActivity ? testContext : Guid.Empty, Trace.CorrelationManager.ActivityId);

        var stop = listener.Events.Single(e => e.EventType == TraceEventType.Stop);
        Assert.Equal(this.request.Method, stop.Message);

        Assert.Equal(2, listener.Transfers.Count);
        var transfer2 = listener.Transfers[1];
        Assert.Equal(SampleParentId, transfer2.RelatedActivityId);
        Assert.Equal(transfer1.RelatedActivityId, transfer2.CurrentActivityId);
    }
}
