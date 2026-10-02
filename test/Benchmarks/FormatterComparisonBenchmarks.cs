// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Microsoft;
using Nerdbank.Streams;
using PolyType;
using StreamJsonRpc;

namespace Benchmarks;

/// <summary>
/// Compares the round-trip cost of an RPC call across each of the formatters that ship in this library.
/// </summary>
/// <remarks>
/// Every benchmark measures a full client-to-server round trip over an in-memory duplex pipe,
/// so the measurement includes serialization, transport, dispatch, and response handling.
/// </remarks>
[MemoryDiagnoser]
[GenerateShapeFor<int>]
[GenerateShapeFor<Workspace>]
public partial class FormatterComparisonBenchmarks
{
    private static readonly Workspace LargePayload = Workspace.Create(seed: 42, projectCount: 12, filesPerProject: 16);

    private JsonRpc clientRpc = null!;
    private JsonRpc serverRpc = null!;
    private IServer client = null!;

    /// <summary>
    /// The server contract exercised by these benchmarks.
    /// </summary>
    public interface IServer
    {
        /// <summary>
        /// A method that takes no arguments and returns nothing.
        /// </summary>
        /// <returns>A task that completes when the server has responded.</returns>
        Task PingAsync();

        /// <summary>
        /// A method that takes two integers and returns their sum.
        /// </summary>
        /// <param name="a">The first addend.</param>
        /// <param name="b">The second addend.</param>
        /// <returns>The sum.</returns>
        Task<int> AddAsync(int a, int b);

        /// <summary>
        /// A method that echoes a large object graph.
        /// </summary>
        /// <param name="workspace">The graph to echo.</param>
        /// <returns>The same graph.</returns>
        Task<Workspace> EchoAsync(Workspace workspace);
    }

    /// <summary>
    /// Gets or sets the formatter under test.
    /// </summary>
    [Params("Newtonsoft", "SystemTextJson", "MessagePackCSharp", "NerdbankMessagePack")]
    public string Formatter { get; set; } = null!;

    /// <summary>
    /// Prepares a connected client and server pair.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        (IDuplexPipe, IDuplexPipe) duplex = FullDuplexStream.CreatePipePair();
        this.clientRpc = new JsonRpc(this.CreateHandler(duplex.Item1));
        this.clientRpc.StartListening();

        this.serverRpc = new JsonRpc(this.CreateHandler(duplex.Item2));
        this.serverRpc.AddLocalRpcTarget<IServer>(new Server(), null);
        this.serverRpc.StartListening();

        this.client = this.clientRpc.Attach<IServer>();

        // Validate that the measured path does real, correct work before measuring it.
        Assumes.True(this.client.AddAsync(2, 3).GetAwaiter().GetResult() == 5);
        Assumes.True(this.client.EchoAsync(LargePayload).GetAwaiter().GetResult().Projects.Count == LargePayload.Projects.Count);
    }

    /// <summary>
    /// Tears down the connection.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        this.clientRpc.Dispose();
        this.serverRpc.Dispose();
    }

    /// <summary>
    /// Round-trips a request with no arguments and no return value.
    /// </summary>
    /// <returns>A task tracking the call.</returns>
    [Benchmark]
    public Task Ping() => this.client.PingAsync();

    /// <summary>
    /// Round-trips a request with two positional integer arguments.
    /// </summary>
    /// <returns>A task tracking the call.</returns>
    [Benchmark]
    public Task<int> Add() => this.client.AddAsync(2, 3);

    /// <summary>
    /// Round-trips a large object graph in both directions.
    /// </summary>
    /// <returns>A task tracking the call.</returns>
    [Benchmark]
    public Task<Workspace> LargePayloadEcho() => this.client.EchoAsync(LargePayload);

    private IJsonRpcMessageHandler CreateHandler(IDuplexPipe pipe)
    {
        return this.Formatter switch
        {
            "Newtonsoft" => new HeaderDelimitedMessageHandler(pipe, new JsonMessageFormatter()),
            "SystemTextJson" => new HeaderDelimitedMessageHandler(pipe, new SystemTextJsonFormatter()),
            "MessagePackCSharp" => new LengthHeaderMessageHandler(pipe, new MessagePackFormatter()),
            "NerdbankMessagePack" => new LengthHeaderMessageHandler(pipe, new NerdbankMessagePackFormatter() { TypeShapeProvider = GeneratedTypeShapeProvider }),
            _ => throw Assumes.NotReachable(),
        };
    }

    private class Server : IServer
    {
        public Task PingAsync() => Task.CompletedTask;

        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task<Workspace> EchoAsync(Workspace workspace) => Task.FromResult(workspace);
    }
}
