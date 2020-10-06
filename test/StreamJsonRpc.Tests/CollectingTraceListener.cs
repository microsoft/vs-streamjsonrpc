using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.Threading;

public class CollectingTraceListener : TraceListener
{
    private readonly StringBuilder lineInProgress = new StringBuilder();

    private readonly ImmutableList<string>.Builder messages = ImmutableList.CreateBuilder<string>();

    public override bool IsThreadSafe => false;

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (this.messages)
            {
                return this.messages.ToImmutable();
            }
        }
    }

    public AsyncAutoResetEvent MessageReceived { get; } = new AsyncAutoResetEvent();

    public override void Write(string message) => this.lineInProgress.Append(message);

    public override void WriteLine(string message)
    {
        this.lineInProgress.Append(message);
        lock (this.messages)
        {
            this.messages.Add(this.lineInProgress.ToString());
            this.MessageReceived.Set();
        }

        this.lineInProgress.Clear();
    }
}
