// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Benchmarks;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // Allow a special "manual" argument for convenient perfview.exe/dotnet-trace-monitored runs for GC pressure analysis.
        // Usage: manual [formatter] [scenario] [iterations]
#if NET
        if (args is ["manual", ..])
        {
            string formatter = args.Length > 1 ? args[1] : "NerdbankMessagePack";
            string scenario = args.Length > 2 ? args[2] : "Ping";
            int iterations = args.Length > 3 ? int.Parse(args[3]) : 1000;

            var b = new FormatterComparisonBenchmarks { Formatter = formatter };
            b.Setup();
            if (scenario is not ("Ping" or "Add" or "Large"))
            {
                throw new ArgumentException($"Unrecognized scenario: {scenario}");
            }

            // Warm up enough to get past tiered JIT before the measured region.
            for (int i = 0; i < 200; i++)
            {
                switch (scenario)
                {
                    case "Ping":
                        await b.Ping();
                        break;
                    case "Add":
                        await b.Add();
                        break;
                    case "Large":
                        await b.LargePayloadEcho();
                        break;
                }
            }

            Console.WriteLine($"PID: {Environment.ProcessId}. Warmed up. Measuring {iterations} iterations of {scenario} over {formatter}.");
            await Task.Delay(2000);

            long before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                switch (scenario)
                {
                    case "Ping":
                        await b.Ping();
                        break;
                    case "Add":
                        await b.Add();
                        break;
                    case "Large":
                        await b.LargePayloadEcho();
                        break;
                }
            }

            sw.Stop();
            long after = GC.GetTotalAllocatedBytes(precise: true);

            Console.WriteLine($"Elapsed: {sw.Elapsed.TotalMilliseconds:F1} ms ({sw.Elapsed.TotalMilliseconds * 1000 / iterations:F2} us/op)");
            Console.WriteLine($"Allocated: {(after - before) / (double)iterations:F0} B/op");
            b.Cleanup();
        }
        else
#endif
        {
            IConfig? config = null;
#if DEBUG
            config = new DebugInProcessConfig();
#endif
            IEnumerable<Summary>? summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        }
    }
}
