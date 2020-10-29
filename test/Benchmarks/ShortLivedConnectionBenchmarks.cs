// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks
{
    using System;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using Nerdbank.Streams;
    using StreamJsonRpc;

    [MemoryDiagnoser]
    public class ShortLivedConnectionBenchmarks
    {
        private const int Iterations = 1000;

        [Benchmark(OperationsPerInvoke = Iterations)]
        public async Task CreateConnectionAndInvokeOnce()
        {
            for (int i = 0; i < Iterations; i++)
            {
                (System.IO.Pipelines.IDuplexPipe, System.IO.Pipelines.IDuplexPipe) duplex = FullDuplexStream.CreatePipePair();
                using var clientRpc = new JsonRpc(new LengthHeaderMessageHandler(duplex.Item1, new MessagePackFormatter()));
                clientRpc.StartListening();

                using var serverRpc = new JsonRpc(new LengthHeaderMessageHandler(duplex.Item2, new MessagePackFormatter()));
                serverRpc.AddLocalRpcTarget(new Server());
                serverRpc.StartListening();

                await clientRpc.InvokeAsync(nameof(Server.NoOp), Array.Empty<object>());
            }
        }

        private class Server
        {
            public void NoOp()
            {
            }
        }
    }
}
