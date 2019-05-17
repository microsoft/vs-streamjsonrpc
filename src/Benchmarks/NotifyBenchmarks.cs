// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Attributes;
    using StreamJsonRpc;

    [MemoryDiagnoser]
    public class NotifyBenchmarks
    {
        private readonly JsonRpc clientRpc;

        public NotifyBenchmarks()
        {
            this.clientRpc = new JsonRpc(Stream.Null, Stream.Null);
        }

        [Benchmark]
        public Task NotifyAsync_NoArgs() => this.clientRpc.NotifyAsync("NoOp", Array.Empty<object>());
    }
}
