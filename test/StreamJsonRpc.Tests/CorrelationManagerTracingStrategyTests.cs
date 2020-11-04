// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
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
}
