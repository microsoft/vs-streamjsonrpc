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
    [InlineData("k=v")]
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

    /// <summary>
    /// Verifies that an inbound request that says nothing about traceparent does not interfere with ongoing activities on the server.
    /// </summary>
    [Theory]
    [CombinatorialData]
    public void Inbound_WithoutTraceParent(bool contextualActivity)
    {
        Guid testActivityId = contextualActivity ? Guid.NewGuid() : Guid.Empty;
        Trace.CorrelationManager.ActivityId = testActivityId;
        try
        {
            using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
            {
                Assert.Equal(testActivityId, Trace.CorrelationManager.ActivityId);
            }

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
    public void Inbound_WithTraceParent_NoContextual(string? traceState)
    {
        this.request.TraceParent = SampleTraceParent;
        this.request.TraceState = traceState;

        using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
        {
            Assert.Equal(SampleParentId, Trace.CorrelationManager.ActivityId);
            Assert.Equal(traceState, CorrelationManagerTracingStrategy.TraceState);
        }

        Assert.Equal(Guid.Empty, Trace.CorrelationManager.ActivityId);
        Assert.Null(CorrelationManagerTracingStrategy.TraceState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("k=v")]
    public void Inbound_WithTraceParent_AndContextual(string? traceState)
    {
        Guid testId = Guid.NewGuid();
        Trace.CorrelationManager.ActivityId = testId;
        try
        {
            this.request.TraceParent = SampleTraceParent;
            this.request.TraceState = traceState;

            using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
            {
                Assert.Equal(SampleParentId, Trace.CorrelationManager.ActivityId);
                Assert.Equal(traceState, CorrelationManagerTracingStrategy.TraceState);
            }

            Assert.Equal(testId, Trace.CorrelationManager.ActivityId);
        }
        finally
        {
            Trace.CorrelationManager.ActivityId = Guid.Empty;
        }
    }

    [Fact]
    public void Activity_Transfers_NoContextual()
    {
        var listener = new ListenerLogger();
        this.strategy.TraceSource = new TraceSource("test", SourceLevels.ActivityTracing)
        {
            Listeners = { listener },
        };

        this.request.TraceParent = SampleTraceParent;
        using (this.strategy.ApplyInboundActivity(this.request))
        {
            Assert.Empty(listener.Transfers);
            var start = Assert.Single(listener.Events);
            Assert.Equal(TraceEventType.Start, start.EventType);
            Assert.Equal(this.request.Method, start.Message);
            listener.Events.Clear();
        }

        var stop = Assert.Single(listener.Events);
        Assert.Equal(TraceEventType.Stop, stop.EventType);
        Assert.Equal(this.request.Method, stop.Message);
    }

    [Fact]
    public void Activity_Transfers_WithContextual()
    {
        var listener = new ListenerLogger();
        this.strategy.TraceSource = new TraceSource("test", SourceLevels.ActivityTracing)
        {
            Listeners = { listener },
        };

        Guid testContext = Guid.NewGuid();
        Trace.CorrelationManager.ActivityId = testContext;

        this.request.TraceParent = SampleTraceParent;
        using (this.strategy.ApplyInboundActivity(this.request))
        {
            var transfer = Assert.Single(listener.Transfers);
            Assert.Equal(testContext, transfer.CurrentActivityId);
            Assert.Equal(SampleParentId, transfer.RelatedActivityId);
            listener.Transfers.Clear();

            var start = listener.Events.Single(e => e.EventType != TraceEventType.Transfer);
            Assert.Equal(TraceEventType.Start, start.EventType);
            Assert.Equal(this.request.Method, start.Message);
            listener.Events.Clear();
        }

        var stop = listener.Events.Single(e => e.EventType != TraceEventType.Transfer);
        Assert.Equal(TraceEventType.Stop, stop.EventType);
        Assert.Equal(this.request.Method, stop.Message);

        var transfer2 = Assert.Single(listener.Transfers);
        Assert.Equal(SampleParentId, transfer2.CurrentActivityId);
        Assert.Equal(testContext, transfer2.RelatedActivityId);
    }

    private class ListenerLogger : TraceListener
    {
        internal List<(Guid RelatedActivityId, Guid CurrentActivityId)> Transfers { get; } = new List<(Guid RelatedActivityId, Guid CurrentActivityId)>();

        internal List<(TraceEventType EventType, string? Message)> Events { get; } = new List<(TraceEventType EventType, string? Message)>();

        public override void TraceTransfer(TraceEventCache eventCache, string source, int id, string message, Guid relatedActivityId)
        {
            base.TraceTransfer(eventCache, source, id, message, relatedActivityId);
            this.Transfers.Add((relatedActivityId, Trace.CorrelationManager.ActivityId));
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id)
        {
            base.TraceEvent(eventCache, source, eventType, id);
            this.Events.Add((eventType, null));
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            base.TraceEvent(eventCache, source, eventType, id, message);
            this.Events.Add((eventType, message));
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            base.TraceEvent(eventCache, source, eventType, id, format, args);
            this.Events.Add((eventType, string.Format(CultureInfo.CurrentCulture, format, args)));
        }

        public override void Write(string message)
        {
        }

        public override void WriteLine(string message)
        {
        }
    }
}
