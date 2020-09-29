using System;
using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

internal class XunitTraceListener : TraceListener
{
    private readonly ITestOutputHelper logger;
    private readonly StringBuilder lineInProgress = new StringBuilder();
    private bool disposed;

    internal XunitTraceListener(ITestOutputHelper logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override bool IsThreadSafe => false;

    public override void Write(string message) => this.lineInProgress.Append(message);

    public override void WriteLine(string message)
    {
        if (!this.disposed)
        {
            this.lineInProgress.Append(message);
            this.logger.WriteLine(this.lineInProgress.ToString());
            this.lineInProgress.Clear();
        }
    }

    protected override void Dispose(bool disposing)
    {
        this.disposed = true;
        base.Dispose(disposing);
    }
}
