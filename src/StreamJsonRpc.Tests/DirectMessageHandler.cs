using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

internal class DirectMessageHandler : DelimitedMessageHandler
{
    public DirectMessageHandler(Stream sendingStream, Stream receivingStream, Encoding encoding) : base(sendingStream, receivingStream, encoding)
    {
    }

    internal AsyncQueue<string> OutboundMessages { get; } = new AsyncQueue<string>();

    internal AsyncQueue<string> IncomingMessages { get; } = new AsyncQueue<string>();

    protected override Task<string> ReadCoreAsync(CancellationToken cancellationToken)
    {
        return this.OutboundMessages.DequeueAsync(cancellationToken);
    }

    protected override Task WriteCoreAsync(string content, Encoding contentEncoding, CancellationToken cancellationToken)
    {
        this.IncomingMessages.Enqueue(content);
        return Task.CompletedTask;
    }
}
