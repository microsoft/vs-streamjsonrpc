// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

public class DirectMessageHandler : DelimitedMessageHandler
{
    public DirectMessageHandler(Stream sendingStream, Stream receivingStream, Encoding encoding)
        : base(sendingStream, receivingStream, encoding)
    {
    }

    internal AsyncQueue<string> MessagesToRead { get; } = new AsyncQueue<string>();

    internal AsyncQueue<string> WrittenMessages { get; } = new AsyncQueue<string>();

    protected override Task<string> ReadCoreAsync(CancellationToken cancellationToken)
    {
        return this.MessagesToRead.DequeueAsync(cancellationToken);
    }

    protected override Task WriteCoreAsync(string content, Encoding contentEncoding, CancellationToken cancellationToken)
    {
        this.WrittenMessages.Enqueue(content);
        return TplExtensions.CompletedTask;
    }
}
