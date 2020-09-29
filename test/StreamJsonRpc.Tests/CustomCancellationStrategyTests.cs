// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class CustomCancellationStrategyTests : TestBase
{
    private readonly JsonRpc clientRpc;
    private readonly Server server;
    private readonly JsonRpc serverRpc;
    private readonly MockCancellationStrategy mockStrategy;

    public CustomCancellationStrategyTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.mockStrategy = new MockCancellationStrategy(this, logger);

        var streams = FullDuplexStream.CreatePair();
        this.clientRpc = new JsonRpc(streams.Item1)
        {
            CancellationStrategy = this.mockStrategy,
            TraceSource = new TraceSource("Client", SourceLevels.Verbose)
            {
                Listeners = { new XunitTraceListener(logger) },
            },
        };
        this.clientRpc.StartListening();

        this.server = new Server();
        this.serverRpc = new JsonRpc(streams.Item2)
        {
            CancellationStrategy = this.mockStrategy,
            TraceSource = new TraceSource("Server", SourceLevels.Verbose)
            {
                Listeners = { new XunitTraceListener(logger) },
            },
        };
        this.serverRpc.AddLocalRpcTarget(this.server);
        this.serverRpc.StartListening();
    }

    /// <summary>
    /// Verifies that cancellation can occur through a custom strategy.
    /// </summary>
    [Fact]
    public async Task CancelRequest_ServerMethodReturns()
    {
        using var cts = new CancellationTokenSource();
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.NoticeCancellationAsync), new object?[] { false }, cancellationToken: cts.Token);
        var completingTask = await Task.WhenAny(invokeTask, this.server.MethodEntered.WaitAsync()).WithCancellation(this.TimeoutToken);
        await completingTask;  // rethrow an exception if there is one.

        cts.Cancel();
        await invokeTask.WithCancellation(this.TimeoutToken);
        Assert.True(this.mockStrategy.CancelRequestMade);
        await this.mockStrategy.OutboundRequestEndedInvoked.WaitAsync(this.TimeoutToken);
    }

    /// <summary>
    /// Verifies that cancellation can occur through a custom strategy.
    /// </summary>
    [Fact]
    public async Task CancelRequest_ServerMethodThrows()
    {
        using var cts = new CancellationTokenSource();
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.NoticeCancellationAsync), new object?[] { true }, cancellationToken: cts.Token);
        var completingTask = await Task.WhenAny(invokeTask, this.server.MethodEntered.WaitAsync()).WithCancellation(this.TimeoutToken);
        await completingTask;  // rethrow an exception if there is one.

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask).WithCancellation(this.TimeoutToken);
        Assert.True(this.mockStrategy.CancelRequestMade);
        await this.mockStrategy.OutboundRequestEndedInvoked.WaitAsync(this.TimeoutToken);
    }

    [Fact]
    public async Task UncanceledRequest_GetsNoClientSideInvocations()
    {
        using var cts = new CancellationTokenSource();
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.EmptyMethod), cancellationToken: cts.Token);

        Assert.False(this.mockStrategy.CancelRequestMade);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.mockStrategy.OutboundRequestEndedInvoked.WaitAsync(ExpectedTimeoutToken));
    }

    /// <summary>
    /// This test attempts to force the timing issue where <see cref="ICancellationStrategy.OutboundRequestEnded(RequestId)"/>
    /// is called before <see cref="ICancellationStrategy.CancelOutboundRequest(RequestId)"/> has finished executing
    /// as this would be most undesirable for any implementation that needs to clean up state in the former that is created by the latter.
    /// </summary>
    [Fact]
    public async Task OutboundCancellationStartAndRequestFinishOverlap()
    {
        using var cts = new CancellationTokenSource();
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.EmptyMethod), cancellationToken: cts.Token);
        var completingTask = await Task.WhenAny(invokeTask, this.server.MethodEntered.WaitAsync()).WithCancellation(this.TimeoutToken);
        await completingTask;  // rethrow an exception if there is one.

        this.mockStrategy.AllowCancelOutboundRequestToExit.Reset();
        cts.Cancel();

        // This may be invoked, but if the product doesn't invoke it, that's ok too.
        ////await this.mockStrategy.OutboundRequestEndedInvoked.WaitAsync(this.TimeoutToken);
    }

#pragma warning disable CA1801 // Review unused parameters
    private class Server
    {
        internal AsyncManualResetEvent MethodEntered { get; } = new AsyncManualResetEvent();

        public async Task NoticeCancellationAsync(bool throwOnCanceled, CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            var canceled = new AsyncManualResetEvent();
            using (cancellationToken.Register(canceled.Set))
            {
                await canceled.WaitAsync();
                if (throwOnCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        public void EmptyMethod(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
        }
    }

    private class MockCancellationStrategy : ICancellationStrategy
    {
        private readonly Dictionary<RequestId, CancellationTokenSource> cancelableRequests = new Dictionary<RequestId, CancellationTokenSource>();
        private readonly CustomCancellationStrategyTests owner;
        private readonly ITestOutputHelper logger;
        private readonly List<RequestId> endedRequestIds = new List<RequestId>();

        internal MockCancellationStrategy(CustomCancellationStrategyTests owner, ITestOutputHelper logger)
        {
            this.owner = owner;
            this.logger = logger;
        }

        internal bool CancelRequestMade { get; private set; }

        internal AsyncAutoResetEvent OutboundRequestEndedInvoked { get; } = new AsyncAutoResetEvent();

        internal ManualResetEventSlim AllowCancelOutboundRequestToExit { get; } = new ManualResetEventSlim(initialState: true);

        public void CancelOutboundRequest(RequestId requestId)
        {
            this.logger.WriteLine($"{nameof(this.CancelOutboundRequest)}({requestId})");
            lock (this.endedRequestIds)
            {
                // We should NEVER be called about a request ID that has already ended.
                // If so, the product can cause a custom cancellation strategy to leak state.
                Assert.DoesNotContain(requestId, this.endedRequestIds);
            }

            CancellationTokenSource? cts;
            lock (this.cancelableRequests)
            {
                this.cancelableRequests.TryGetValue(requestId, out cts);
            }

            cts?.Cancel();
            this.CancelRequestMade = true;

            // Wait for the out of order invocation to happen if it's possible,
            // so the OutboundCancellationStartAndRequestFinishOverlap test can catch it.
            // Otherwise timeout, which is necessary to avoid a test hang when the product DOES work,
            // since it shouldn't allow OutboundRequestEnded to execute before this method exits.
            if (!this.AllowCancelOutboundRequestToExit.Wait(ExpectedTimeout))
            {
                this.logger.WriteLine("Timed out waiting for " + nameof(this.AllowCancelOutboundRequestToExit) + " to be signaled (good thing).");
            }
        }

        public void OutboundRequestEnded(RequestId requestId)
        {
            this.logger.WriteLine($"{nameof(this.OutboundRequestEnded)}({requestId}) invoked.");
            lock (this.endedRequestIds)
            {
                this.endedRequestIds.Add(requestId);
            }

            this.OutboundRequestEndedInvoked.Set();
        }

        public void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource)
        {
            this.logger.WriteLine($"{nameof(this.IncomingRequestStarted)}({requestId})");
            lock (this.cancelableRequests)
            {
                this.cancelableRequests.Add(requestId, cancellationTokenSource);
            }
        }

        public void IncomingRequestEnded(RequestId requestId)
        {
            this.logger.WriteLine($"{nameof(this.IncomingRequestEnded)}({requestId})");
            lock (this.cancelableRequests)
            {
                this.cancelableRequests.Remove(requestId);
            }
        }
    }
}
