// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class ActivityTracingStrategyTests : TestBase
{
    private readonly ActivityTracingStrategy strategy = new ActivityTracingStrategy();
    private readonly JsonRpcRequest request = new JsonRpcRequest { Method = "test" };

    public ActivityTracingStrategyTests(ITestOutputHelper logger)
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

    [Fact]
    public void Outbound_WithContextualActivity_WrongFormat_IsIgnored()
    {
        Activity.Current = new Activity("test").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        try
        {
            this.strategy.ApplyOutboundActivity(this.request);
            Assert.Null(this.request.TraceParent);
            Assert.Null(this.request.TraceState);
        }
        finally
        {
            Activity.Current.Stop();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k=v")]
    [InlineData("k=v,k2=v2")]
    public void Outbound_WithContextualActivity(string? traceState)
    {
        Activity.Current = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        Activity.Current.TraceStateString = traceState;
        try
        {
            this.strategy.ApplyOutboundActivity(this.request);
            Assert.Equal(Activity.Current.Id, this.request.TraceParent);
            Assert.Equal(traceState, this.request.TraceState);
        }
        finally
        {
            Activity.Current.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inbound_WithoutTraceParent(bool contextualActivity)
    {
        Activity? testActivity = contextualActivity ? new Activity("test").Start() : null;
        try
        {
            using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
            {
                Assert.Same(testActivity, Activity.Current?.Parent);
            }

            Assert.Same(testActivity, Activity.Current);
        }
        finally
        {
            testActivity?.Stop();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("k=v")]
    public void Inbound_WithTraceParent_NoContextual(string? traceState)
    {
        this.request.TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        this.request.TraceState = traceState;

        using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
        {
            Assert.Equal(this.request.TraceParent, Activity.Current?.ParentId);
            Assert.Equal(traceState, Activity.Current?.TraceStateString);
        }

        Assert.Null(Activity.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("k=v")]
    public void Inbound_WithTraceParent_AndContextual(string? traceState)
    {
        Activity testActivity = new Activity("test").Start();
        try
        {
            this.request.TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
            this.request.TraceState = traceState;

            using (IDisposable? state = this.strategy.ApplyInboundActivity(this.request))
            {
                Assert.Equal(this.request.TraceParent, Activity.Current?.ParentId);
                Assert.Equal(traceState, Activity.Current?.TraceStateString);
            }

            Assert.Same(testActivity, Activity.Current);
        }
        finally
        {
            testActivity.Stop();
        }
    }
}
