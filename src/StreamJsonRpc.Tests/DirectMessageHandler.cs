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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

public class DirectMessageHandler : MessageHandlerBase
{
    private JsonMessageFormatter formatter = new JsonMessageFormatter();

    public DirectMessageHandler()
    {
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    internal AsyncQueue<JToken> MessagesToRead { get; } = new AsyncQueue<JToken>();

    internal AsyncQueue<JToken> WrittenMessages { get; } = new AsyncQueue<JToken>();

    protected override bool CanFlushConcurrentlyWithOtherWrites => true;

    protected override async ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken)
    {
        return this.formatter.Deserialize(await this.MessagesToRead.DequeueAsync(cancellationToken));
    }

    protected override ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        this.WrittenMessages.Enqueue(this.formatter.Serialize(content));
        return default;
    }

    protected override ValueTask FlushAsync(CancellationToken cancellationToken) => default;
}
