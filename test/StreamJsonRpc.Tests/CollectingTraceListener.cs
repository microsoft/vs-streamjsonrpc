using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

public class CollectingTraceListener : TraceListener
{
    private readonly StringBuilder lineInProgress = new StringBuilder();

    private readonly ImmutableList<string>.Builder messages = ImmutableList.CreateBuilder<string>();

    private readonly ImmutableList<JsonRpc.TraceEvents>.Builder traceEventIds = ImmutableList.CreateBuilder<JsonRpc.TraceEvents>();

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

    public ImmutableList<JsonRpc.TraceEvents> Ids
    {
        get
        {
            lock (this.traceEventIds)
            {
                return this.traceEventIds.ToImmutable();
            }
        }
    }

    public AsyncAutoResetEvent MessageReceived { get; } = new AsyncAutoResetEvent();

    public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
    {
        lock (this.traceEventIds)
        {
            this.traceEventIds.Add((JsonRpc.TraceEvents)id);
        }

        base.TraceEvent(eventCache, source, eventType, id, message);
    }

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
