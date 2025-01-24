// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Microsoft;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Benchmarks;

[MemoryDiagnoser]
public class ShortLivedConnectionBenchmarks
{
    private const int Iterations = 1000;

    [Params("JSON", "MessagePack", "NerdbankMessagePack")]
    public string Formatter { get; set; } = null!;

    [Benchmark(OperationsPerInvoke = Iterations)]
    public async Task CreateConnectionAndInvokeOnce()
    {
        for (int i = 0; i < Iterations; i++)
        {
            (IDuplexPipe, IDuplexPipe) duplex = FullDuplexStream.CreatePipePair();
            using var clientRpc = new JsonRpc(CreateHandler(duplex.Item1));
            clientRpc.StartListening();

            using var serverRpc = new JsonRpc(CreateHandler(duplex.Item2));
            serverRpc.AddLocalRpcTarget(new Server());
            serverRpc.StartListening();

            await clientRpc.InvokeAsync(nameof(Server.NoOp), Array.Empty<object>());
        }

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

    private class Server
    {
        public void NoOp()
        {
        }
    }
}
