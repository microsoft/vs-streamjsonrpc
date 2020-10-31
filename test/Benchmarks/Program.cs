// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using BenchmarkDotNet.Configs;
    using BenchmarkDotNet.Reports;
    using BenchmarkDotNet.Running;

    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            // Allow a special "manual" argument for convenient perfview.exe-monitored runs for GC pressure analysis.
            if (args.Length == 1 && args[0] == "manual")
            {
                var b = new InvokeBenchmarks { Formatter = "MessagePack" };
                b.Setup();
                await b.InvokeAsync_NoArgs();

                await Task.Delay(2000);

                for (int i = 0; i < 1000; i++)
                {
                    await b.InvokeAsync_NoArgs();
                }
            }
            else
            {
                IConfig? config = null;
#if DEBUG
                config = new DebugInProcessConfig();
#endif
                IEnumerable<Summary>? summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
            }
        }
    }
}
