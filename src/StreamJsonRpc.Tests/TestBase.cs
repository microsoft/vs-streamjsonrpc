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
    private static TimeSpan TestTimeout => Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource timeoutTokenSource;

    protected TestBase(ITestOutputHelper logger)
    {
        this.Logger = logger;
        this.timeoutTokenSource = new CancellationTokenSource(TestTimeout);
    }

    protected ITestOutputHelper Logger { get; }

    protected CancellationToken TimeoutToken => this.timeoutTokenSource.Token;

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
}
