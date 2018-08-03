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
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

public class DirectMessageHandler : DelimitedMessageHandler
{
    public DirectMessageHandler(Stream sendingStream, Stream receivingStream, Encoding encoding)
        : base(sendingStream, receivingStream, encoding)
    {
    }

    internal AsyncQueue<JToken> MessagesToRead { get; } = new AsyncQueue<JToken>();

    internal AsyncQueue<JToken> WrittenMessages { get; } = new AsyncQueue<JToken>();

    protected override async ValueTask<JToken> ReadCoreAsync(CancellationToken cancellationToken)
    {
        return await this.MessagesToRead.DequeueAsync(cancellationToken);
    }

    protected override ValueTask WriteCoreAsync(JToken content, Encoding contentEncoding, CancellationToken cancellationToken)
    {
        this.WrittenMessages.Enqueue(content);
        return default(ValueTask);
    }
}
