// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Attributes;
using Microsoft;
using StreamJsonRpc;

namespace Benchmarks;

[MemoryDiagnoser]
public class NotifyBenchmarks
{
    private JsonRpc clientRpc = null!;

    [Params("JSON", "MessagePack", "NerdbankMessagePack")]
    public string Formatter { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        this.clientRpc = new JsonRpc(CreateHandler(Stream.Null));

        IJsonRpcMessageHandler CreateHandler(Stream pipe)
        {
            return this.Formatter switch
            {
                "JSON" => new HeaderDelimitedMessageHandler(pipe, new JsonMessageFormatter()),
                "MessagePack" => new LengthHeaderMessageHandler(pipe, pipe, new MessagePackFormatter()),
                "NerdbankMessagePack" => new LengthHeaderMessageHandler(pipe, pipe, new NerdbankMessagePackFormatter()),
                _ => throw Assumes.NotReachable(),
            };
        }
    }

    [Benchmark]
    public Task NotifyAsync_NoArgs() => this.clientRpc.NotifyAsync("NoOp", Array.Empty<object>());
}
