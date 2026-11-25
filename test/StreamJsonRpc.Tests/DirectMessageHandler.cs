// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

public class DirectMessageHandler : MessageHandlerBase
{
    public DirectMessageHandler(IJsonRpcMessageTextFormatter formatter)
        : base(formatter)
    {
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public new IJsonRpcMessageTextFormatter Formatter => (IJsonRpcMessageTextFormatter)base.Formatter;

    internal AsyncQueue<JToken> MessagesToRead { get; } = new AsyncQueue<JToken>();

    internal AsyncQueue<JToken> WrittenMessages { get; } = new AsyncQueue<JToken>();

    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        string json = (await this.MessagesToRead.DequeueAsync(cancellationToken)).ToString(Formatting.None);
        byte[] bytes = this.Formatter.Encoding.GetBytes(json);
        return this.Formatter.Deserialize(new ReadOnlySequence<byte>(bytes));
    }

    protected override ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        using var buffer = new Sequence<byte>();
        this.Formatter.Serialize(buffer, content);
        string json = this.Formatter.Encoding.GetString(buffer.AsReadOnlySequence.ToArray());
        this.WrittenMessages.Enqueue(JToken.Parse(json));
        return default;
    }

    protected override ValueTask FlushAsync(CancellationToken cancellationToken) => default;
}
