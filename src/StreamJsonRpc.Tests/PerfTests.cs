// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Nerdbank;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

[Trait("Category", "SkipWhenLiveUnitTesting")] // very slow test
public class PerfTests
{
    private readonly ITestOutputHelper logger;

    public PerfTests(ITestOutputHelper logger)
    {
        this.logger = logger;
    }

    [Fact]
    public async Task ChattyPerf_OverNamedPipes()
    {
        string pipeName = Guid.NewGuid().ToString();
        var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, /*maxConnections*/ 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
#if NET452
        var connectTask = Task.Run(() => serverPipe.WaitForConnection());
#else
        var connectTask = serverPipe.WaitForConnectionAsync();
#endif
        var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
#if NET452
        clientPipe.Connect();
#else
        await clientPipe.ConnectAsync();
#endif
        await connectTask; // rethrow any exception
        await this.ChattyPerfAsync(serverPipe, clientPipe);
    }

    [Fact]
    public async Task ChattyPerf_OverFullDuplexStream()
    {
        var streams = FullDuplexStream.CreateStreams();
        await this.ChattyPerfAsync(streams.Item1, streams.Item2);
    }

    private async Task ChattyPerfAsync(Stream serverStream, Stream clientStream)
    {
        using (JsonRpc.Attach(serverStream, new Server()))
        using (var client = JsonRpc.Attach(clientStream))
        {
            // warmup
            await client.InvokeAsync("NoOp");

            const int maxIterations = 10000;
            var timer = Stopwatch.StartNew();
            int i;
            for (i = 0; i < maxIterations; i++)
            {
                await client.InvokeAsync("NoOp");

                if (timer.ElapsedMilliseconds > 2000 && i > 0)
                {
                    // It's taking too long to reach maxIterations. Break out.
                    break;
                }
            }

            timer.Stop();
            this.logger.WriteLine($"{i} iterations completed in {timer.ElapsedMilliseconds} ms.");
            this.logger.WriteLine($"Rate: {i / timer.Elapsed.TotalSeconds} invocations per second.");
            this.logger.WriteLine($"Overhead: {(double)timer.ElapsedMilliseconds / i} ms per invocation.");
        }
    }

    public class Server
    {
        public void NoOp()
        {
        }
    }
}
