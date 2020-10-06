// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

[Trait("Category", "SkipWhenLiveUnitTesting")] // very slow test
[Trait("FailsOnMono", "true")]
public class PerfTests
{
    private readonly ITestOutputHelper logger;

    public PerfTests(ITestOutputHelper logger)
    {
        this.logger = logger;
    }

    public static object[][] PipeTypes
    {
        get
        {
            var duplex = FullDuplexStream.CreateStreams();
            var pipes = GetNamedPipes();
            return new object[][]
            {
                new object[] { duplex.Item1, duplex.Item2 },
                new object[] { pipes.Item1, pipes.Item2 },
            };
        }
    }

    [Theory]
    [MemberData(nameof(PipeTypes))]
    public async Task ChattyPerf(Stream serverStream, Stream clientStream)
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

    [Theory]
    [MemberData(nameof(PipeTypes))]
    public async Task BurstNotifyMessages(Stream serverStream, Stream clientStream)
    {
        var notifyServer = new NotifyServer();
        using (var serverRpc = JsonRpc.Attach(serverStream, notifyServer))
        using (var clientRpc = JsonRpc.Attach(clientStream))
        {
            serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Warning);
            clientRpc.TraceSource = new TraceSource("Server", SourceLevels.Warning);
            serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.logger));
            clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.logger));

            // warmup
            await clientRpc.InvokeAsync("NoOp");

            const int maxIterations = 10000;
            var notifyTasks = new List<Task>(maxIterations);
            var timer = Stopwatch.StartNew();
            int i;
            for (i = 0; i < maxIterations; i++)
            {
                notifyTasks.Add(clientRpc.NotifyAsync("NoOp"));

                if (timer.ElapsedMilliseconds > 2000 && i > 0)
                {
                    // It's taking too long to reach maxIterations. Break out.
                    break;
                }
            }

            notifyServer.RequestSignalAfter(notifyTasks.Count);
            await Task.WhenAll(notifyTasks);
            await notifyServer.Signal;

            timer.Stop();
            this.logger.WriteLine($"{i} iterations completed in {timer.ElapsedMilliseconds} ms.");
            this.logger.WriteLine($"Rate: {i / timer.Elapsed.TotalSeconds} invocations per second.");
            this.logger.WriteLine($"Overhead: {(double)timer.ElapsedMilliseconds / i} ms per invocation.");
        }
    }

    [Theory]
    [MemberData(nameof(PipeTypes))]
    public async Task BurstInvokeMessages(Stream serverStream, Stream clientStream)
    {
        var server = new Server();
        using (var serverRpc = JsonRpc.Attach(serverStream, server))
        using (var clientRpc = JsonRpc.Attach(clientStream))
        {
            serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Warning);
            clientRpc.TraceSource = new TraceSource("Server", SourceLevels.Warning);
            serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.logger));
            clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.logger));

            // warmup
            await clientRpc.InvokeAsync("NoOp");

            const int maxIterations = 10000;
            var invokeTasks = new List<Task>(maxIterations);
            var timer = Stopwatch.StartNew();
            int i;
            for (i = 0; i < maxIterations; i++)
            {
                invokeTasks.Add(clientRpc.InvokeAsync("NoOp"));

                if (timer.ElapsedMilliseconds > 2000 && i > 0)
                {
                    // It's taking too long to reach maxIterations. Break out.
                    break;
                }
            }

            await Task.WhenAll(invokeTasks);

            timer.Stop();
            this.logger.WriteLine($"{i} iterations completed in {timer.ElapsedMilliseconds} ms.");
            this.logger.WriteLine($"Rate: {i / timer.Elapsed.TotalSeconds} invocations per second.");
            this.logger.WriteLine($"Overhead: {(double)timer.ElapsedMilliseconds / i} ms per invocation.");
        }
    }

#pragma warning disable SA1414 // Tuple types in signatures should have element names
    private static (Stream, Stream) GetNamedPipes()
#pragma warning restore SA1414 // Tuple types in signatures should have element names
    {
        string pipeName = Guid.NewGuid().ToString();
        var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, /*maxConnections*/ 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connectTask = Task.Run(() => serverPipe.WaitForConnection());
        var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        clientPipe.Connect();
        connectTask.GetAwaiter().GetResult(); // rethrow any exception

        return (serverPipe, clientPipe);
    }

    public class Server
    {
        public void NoOp()
        {
        }
    }

    public class NotifyServer
    {
        internal readonly AsyncManualResetEvent Signal = new AsyncManualResetEvent();
        internal int InvocationCounter;
        private int signalAfter;

        public void NoOp()
        {
            if (Interlocked.Increment(ref this.InvocationCounter) >= this.signalAfter && this.signalAfter > 0)
            {
                this.Signal.Set();
            }
        }

        internal void RequestSignalAfter(int count)
        {
            Volatile.Write(ref this.signalAfter, count);
            if (Volatile.Read(ref this.InvocationCounter) >= this.signalAfter)
            {
                this.Signal.Set();
            }
        }
    }
}
