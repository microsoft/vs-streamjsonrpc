// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;

public class FlexibleNamedArgumentMatchingTests : TestBase
{
    public FlexibleNamedArgumentMatchingTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    private interface IInterfaceDefaultTarget
    {
        int GetValue(int value = 7);
    }

    private interface IImplementationDefaultTarget
    {
        int GetValue(int value);
    }

    private interface IConflictingDefaultTargetOne
    {
        Task<int> GetValueAsync(int value = 1);
    }

    private interface IConflictingDefaultTargetTwo
    {
        Task<int> GetValueAsync(int value = 2);
    }

    private interface IMatchingDefaultTargetOne
    {
        int GetValue(int value = 7);
    }

    private interface IMatchingDefaultTargetTwo
    {
        int GetValue(int value = 7);
    }

    [Fact]
    public async Task InterfaceDefaultValueTakesPrecedence()
    {
        using RpcPair rpc = this.CreateContractRpcPair<IInterfaceDefaultTarget>(new InterfaceDefaultTarget());
        int result = await rpc.Client.InvokeWithParameterObjectAsync<int>(
            nameof(IInterfaceDefaultTarget.GetValue),
            NamedArgs.Create(new { }),
            this.TimeoutToken);

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task ImplementationDefaultValueIsFallback()
    {
        using RpcPair rpc = this.CreateContractRpcPair<IImplementationDefaultTarget>(new ImplementationDefaultTarget());
        int result = await rpc.Client.InvokeWithParameterObjectAsync<int>(
            nameof(IImplementationDefaultTarget.GetValue),
            NamedArgs.Create(new { unknown = true }),
            this.TimeoutToken);

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task ClassRegistrationUsesInterfaceDefaultValue()
    {
        using RpcPair rpc = this.CreateRpcPair(new InterfaceDefaultTarget());
        int result = await rpc.Client.InvokeWithParameterObjectAsync<int>(
            nameof(InterfaceDefaultTarget.GetValue),
            NamedArgs.Create(new { unknown = true }),
            this.TimeoutToken);

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task ConflictingInterfaceDefaultsWarnOnceAndUseImplementationDefault()
    {
        var target = new ConflictingInterfaceDefaultTarget();
        using RpcPair rpc = this.CreateRpcPair(target, out CollectingTraceListener traces);
        int result = await rpc.Client.InvokeWithParameterObjectAsync<int>(
            nameof(ConflictingInterfaceDefaultTarget.GetValueAsync),
            NamedArgs.Create(new { unknown = true }),
            this.TimeoutToken);

        Assert.Equal(3, result);
        Assert.Single(traces.Ids, id => id == JsonRpc.TraceEvents.ConflictingParameterDefaultValues);
        Assert.Contains(traces.Events, e => e.EventType == TraceEventType.Warning && e.Message?.Contains(nameof(ConflictingInterfaceDefaultTarget.GetValueAsync), StringComparison.Ordinal) is true);
    }

    [Fact]
    public async Task MatchingInterfaceDefaultsDoNotWarn()
    {
        using RpcPair rpc = this.CreateRpcPair(new MatchingInterfaceDefaultTarget(), out CollectingTraceListener traces);
        int result = await rpc.Client.InvokeWithParameterObjectAsync<int>(
            nameof(MatchingInterfaceDefaultTarget.GetValue),
            NamedArgs.Create(new { unknown = true }),
            this.TimeoutToken);

        Assert.Equal(7, result);
        Assert.DoesNotContain(JsonRpc.TraceEvents.ConflictingParameterDefaultValues, traces.Ids);
    }

    [Fact]
    public void OverloadsAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new OverloadedTarget(), options));
        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void CancellationTokenOverloadsAreAllowed()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };

        server.AddLocalRpcTarget(new CancellationTokenOverloadedTarget(), options);
    }

    private RpcPair CreateContractRpcPair<TContract>(TContract target)
        where TContract : class
    {
        (Stream serverStream, Stream clientStream) = Nerdbank.FullDuplexStream.CreateStreams();
        var server = new JsonRpc(serverStream);
        server.AddLocalRpcTarget<TContract>(target, new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
        var client = new JsonRpc(clientStream);
        server.StartListening();
        client.StartListening();
        return new RpcPair(server, client, serverStream, clientStream);
    }

    private RpcPair CreateRpcPair<TTarget>(TTarget target)
        where TTarget : class
    {
        return this.CreateRpcPair(target, out _);
    }

    private RpcPair CreateRpcPair<TTarget>(TTarget target, out CollectingTraceListener traces)
        where TTarget : class
    {
        (Stream serverStream, Stream clientStream) = Nerdbank.FullDuplexStream.CreateStreams();
        var server = new JsonRpc(serverStream)
        {
            TraceSource = new TraceSource("FlexibleNamedArgumentMatchingTests", SourceLevels.Warning),
        };
        traces = new CollectingTraceListener();
        server.TraceSource.Listeners.Add(traces);
        server.AddLocalRpcTarget(target, new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
        var client = new JsonRpc(clientStream);
        server.StartListening();
        client.StartListening();
        return new RpcPair(server, client, serverStream, clientStream);
    }

    private sealed class InterfaceDefaultTarget : IInterfaceDefaultTarget
    {
        public int GetValue(int value = 3) => value;
    }

    private sealed class ImplementationDefaultTarget : IImplementationDefaultTarget
    {
        public int GetValue(int value = 3) => value;
    }

    private sealed class ConflictingInterfaceDefaultTarget : IConflictingDefaultTargetOne, IConflictingDefaultTargetTwo
    {
        public Task<int> GetValueAsync(int value = 3) => Task.FromResult(value);
    }

    private sealed class MatchingInterfaceDefaultTarget : IMatchingDefaultTargetOne, IMatchingDefaultTargetTwo
    {
        public int GetValue(int value = 3) => value;
    }

    private sealed class OverloadedTarget
    {
        [JsonRpcMethod("Select")]
        public string SelectExact(int value) => "exact";

        [JsonRpcMethod("Select")]
        public string SelectFlexible(int value, int missing) => "flexible";
    }

    private sealed class CancellationTokenOverloadedTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int value) => "non-cancelable";

        [JsonRpcMethod("Select")]
        public string Select(int value, CancellationToken cancellationToken) => "cancelable";
    }

    private sealed class RpcPair : IDisposable
    {
        private readonly Stream serverStream;
        private readonly Stream clientStream;

        internal RpcPair(JsonRpc server, JsonRpc client, Stream serverStream, Stream clientStream)
        {
            this.Server = server;
            this.Client = client;
            this.serverStream = serverStream;
            this.clientStream = clientStream;
        }

        internal JsonRpc Server { get; }

        internal JsonRpc Client { get; }

        public void Dispose()
        {
            this.Client.Dispose();
            this.Server.Dispose();
            this.clientStream.Dispose();
            this.serverStream.Dispose();
        }
    }
}
