// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
        this.mockStrategy = new MockCancellationStrategy(logger);

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
    public async Task CancelRequest()
    {
        using var cts = new CancellationTokenSource();
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.NoticeCancellationAsync), cancellationToken: cts.Token);
        var completingTask = await Task.WhenAny(invokeTask, this.server.MethodEntered.WaitAsync()).WithCancellation(this.TimeoutToken);
        await completingTask;  // rethrow an exception if there is one.

        cts.Cancel();
        await invokeTask.WithCancellation(this.TimeoutToken);
        Assert.True(this.mockStrategy.CancelRequestMade);
    }

    private class Server
    {
        internal AsyncManualResetEvent MethodEntered { get; } = new AsyncManualResetEvent();

        public async Task NoticeCancellationAsync(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            var canceled = new AsyncManualResetEvent();
            using (cancellationToken.Register(canceled.Set))
            {
                await canceled.WaitAsync();
            }
        }
    }

    private class MockCancellationStrategy : ICancellationStrategy
    {
        private readonly Dictionary<RequestId, CancellationTokenSource> cancelableRequests = new Dictionary<RequestId, CancellationTokenSource>();
        private readonly ITestOutputHelper logger;

        internal MockCancellationStrategy(ITestOutputHelper logger)
        {
            this.logger = logger;
        }

        internal bool CancelRequestMade { get; private set; }

        public void CancelOutboundRequest(RequestId requestId)
        {
            this.logger.WriteLine("Canceling outbound request: {0}", requestId);
            CancellationTokenSource? cts;
            lock (this.cancelableRequests)
            {
                this.cancelableRequests.TryGetValue(requestId, out cts);
            }

            cts?.Cancel();
            this.CancelRequestMade = true;
        }

        public void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource)
        {
            this.logger.WriteLine("Recognizing incoming request start: {0}", requestId);
            lock (this.cancelableRequests)
            {
                this.cancelableRequests.Add(requestId, cancellationTokenSource);
            }
        }

        public void IncomingRequestEnded(RequestId requestId)
        {
            this.logger.WriteLine("Recognizing incoming request end: {0}", requestId);
            lock (this.cancelableRequests)
            {
                this.cancelableRequests.Remove(requestId);
            }
        }
    }
}
