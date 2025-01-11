// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Microsoft;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Benchmarks;

[MemoryDiagnoser]
public class InvokeBenchmarks
{
    private JsonRpc clientRpc = null!;
    private JsonRpc serverRpc = null!;

    [Params("JSON", "MessagePack", "NerdbankMessagePack")]
    public string Formatter { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        (IDuplexPipe, IDuplexPipe) duplex = FullDuplexStream.CreatePipePair();
        this.clientRpc = new JsonRpc(CreateHandler(duplex.Item1));
        this.clientRpc.StartListening();

        this.serverRpc = new JsonRpc(CreateHandler(duplex.Item2));
        this.serverRpc.AddLocalRpcTarget(new Server());
        this.serverRpc.StartListening();

        IJsonRpcMessageHandler CreateHandler(IDuplexPipe pipe)
        {
            return this.Formatter switch
            {
                "JSON" => new HeaderDelimitedMessageHandler(pipe, new JsonMessageFormatter()),
                "MessagePack" => new LengthHeaderMessageHandler(pipe, new MessagePackFormatter()),
                "NerdbankMessagePack" => new LengthHeaderMessageHandler(pipe, new NerdbankMessagePackFormatter()),
                _ => throw Assumes.NotReachable(),
            };
        }
    }

    [Benchmark]
    public Task InvokeAsync_NoArgs() => this.clientRpc.InvokeAsync(nameof(Server.NoOp), Array.Empty<object>());

    private class Server
    {
        public void NoOp()
        {
        }
    }
}
