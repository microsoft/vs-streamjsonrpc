using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nerdbank;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class PerfTests
{
    private readonly ITestOutputHelper logger;

    public PerfTests(ITestOutputHelper logger)
    {
        this.logger = logger;
    }

    [Fact]
    public async Task ChattyPerf()
    {
        var streams = FullDuplexStream.CreateStreams();
        JsonRpc.Attach(streams.Item1, new Server());
        var client = JsonRpc.Attach(streams.Item2);

        const int iterations = 10000;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await client.InvokeAsync("NoOp");
        }

        timer.Stop();
        this.logger.WriteLine($"{iterations} iterations completed in {timer.ElapsedMilliseconds} ms.");
        this.logger.WriteLine($"Rate: {iterations / timer.Elapsed.TotalSeconds} invocations per second.");
        this.logger.WriteLine($"Overhead: {(double)timer.ElapsedMilliseconds / iterations} ms per invocation.");
    }

    public class Server
    {
        public void NoOp() { }
    }
}
