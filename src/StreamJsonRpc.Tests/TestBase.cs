// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

public abstract class TestBase : IDisposable
{
    protected static readonly TimeSpan ExpectedTimeout = TimeSpan.FromMilliseconds(200);

    protected static readonly TimeSpan UnexpectedTimeout = Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);

    protected static readonly CancellationToken PrecanceledToken = new CancellationToken(canceled: true);

    private const int GCAllocationAttempts = 10;

    private readonly CancellationTokenSource timeoutTokenSource;

    protected TestBase(ITestOutputHelper logger)
    {
        this.Logger = logger;
        this.timeoutTokenSource = new CancellationTokenSource(TestTimeout);
        this.timeoutTokenSource.Token.Register(() => this.Logger.WriteLine($"TEST TIMEOUT: {nameof(TestBase)}.{nameof(this.TimeoutToken)} has been canceled due to the test exceeding the {TestTimeout} time limit."));
    }

    protected static CancellationToken ExpectedTimeoutToken => new CancellationTokenSource(ExpectedTimeout).Token;

    protected static CancellationToken UnexpectedTimeoutToken => new CancellationTokenSource(UnexpectedTimeout).Token;

    protected ITestOutputHelper Logger { get; }

    protected CancellationToken TimeoutToken => Debugger.IsAttached ? CancellationToken.None : this.timeoutTokenSource.Token;

    private static TimeSpan TestTimeout => UnexpectedTimeout;

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.timeoutTokenSource.Dispose();
        }
    }

    /// <summary>
    /// Runs a given scenario many times to observe memory characteristics and assert that they can satisfy given conditions.
    /// </summary>
    /// <param name="scenario">The delegate to invoke.</param>
    /// <param name="maxBytesAllocated">The maximum number of bytes allowed to be allocated by one run of the scenario. Use -1 to indicate no limit.</param>
    /// <param name="iterations">The number of times to invoke <paramref name="scenario"/> in a row before measuring average memory impact.</param>
    /// <param name="allowedAttempts">The number of times the (scenario * iterations) loop repeats with a failing result before ultimately giving up.</param>
    /// <returns>A task that captures the result of the operation.</returns>
    protected async Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated = -1, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
    {
        // prime the pump
        for (int i = 0; i < 3; i++)
        {
            await scenario();
        }

        // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
        bool passingAttemptObserved = false;
        for (int attempt = 1; attempt <= allowedAttempts; attempt++)
        {
            this.Logger?.WriteLine($"Attempt {attempt}");
            long initialMemory = GC.GetTotalMemory(forceFullCollection: true);
            for (int i = 0; i < iterations; i++)
            {
                await scenario();
            }

            long allocated = (GC.GetTotalMemory(forceFullCollection: false) - initialMemory) / iterations;
            long leaked = (GC.GetTotalMemory(forceFullCollection: true) - initialMemory) / iterations;

            this.Logger?.WriteLine($"{leaked} bytes leaked per iteration.", leaked);
            this.Logger?.WriteLine($"{allocated} bytes allocated per iteration ({(maxBytesAllocated >= 0 ? (object)maxBytesAllocated : "unlimited")} allowed).");

            if (leaked <= 0 && (allocated <= maxBytesAllocated || maxBytesAllocated < 0))
            {
                passingAttemptObserved = true;
                break;
            }

            if (!passingAttemptObserved)
            {
                // give the system a bit of cool down time to increase the odds we'll pass next time.
                GC.Collect();
                await Task.Delay(250);
            }
        }

        Assert.True(passingAttemptObserved);
    }
}
