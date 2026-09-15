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
    public async Task BaseClassRegistrationUsesRuntimeOverrideInterfaceDefault()
    {
        (Stream serverStream, Stream clientStream) = Nerdbank.FullDuplexStream.CreateStreams();
        using var server = new JsonRpc(serverStream);
        server.AddLocalRpcTarget(typeof(VirtualBaseTarget), new VirtualDerivedTarget(), new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
        using var client = new JsonRpc(clientStream);
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeWithParameterObjectAsync<int>(
            nameof(VirtualBaseTarget.GetValue),
            NamedArgs.Create(new { }),
            this.TimeoutToken);

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task BaseClassRegistrationUsesExposedContractDefault()
    {
        (Stream serverStream, Stream clientStream) = Nerdbank.FullDuplexStream.CreateStreams();
        using var server = new JsonRpc(serverStream);
        server.AddLocalRpcTarget(typeof(BaseContractDefaultTarget), new DerivedContractDefaultTarget(), new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
        using var client = new JsonRpc(clientStream);
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeWithParameterObjectAsync<int>(
            nameof(BaseContractDefaultTarget.GetValue),
            NamedArgs.Create(new { }),
            this.TimeoutToken);

        Assert.Equal(7, result);
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

        server.AddLocalRpcTarget(new NonCancelableTarget(), options);
        server.AddLocalRpcTarget(new CancelableTarget(), options);
    }

    [Fact]
    public void CancellationTokenOverloadsWithDifferentParameterNamesAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };
        server.AddLocalRpcTarget(new FirstNamedTarget(), options);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new DifferentlyNamedCancelableTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void MultipleCancellationTokenOverloadsAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new MultipleCancelableTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void StrictCancellationTokenBatchAfterFlexibleTargetIsRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        server.AddLocalRpcTarget(new NonCancelableTarget(), new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new StrictMultipleCancelableTarget()));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void FlexibleTargetAfterStrictCancellationTokenBatchIsRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        server.AddLocalRpcTarget(new StrictMultipleCancelableTarget());

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(
            new NonCancelableTarget(),
            new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true }));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public async Task MixedModeCancellationTokenPairPreservesRegistrationOrder()
    {
        (Stream serverStream, Stream clientStream) = Nerdbank.FullDuplexStream.CreateStreams();
        using var server = new JsonRpc(serverStream);
        server.AddLocalRpcTarget(new CancelableTarget(), new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true });
        server.AddLocalRpcTarget(new NonCancelableTarget());
        using var client = new JsonRpc(clientStream);
        server.StartListening();
        client.StartListening();

        string result = await client.InvokeWithParameterObjectAsync<string>(
            "Select",
            NamedArgs.Create(new { value = 1 }),
            this.TimeoutToken);

        Assert.Equal("cancelable", result);
    }

    [Fact]
    public void SameTypedOverloadsWithDifferentParameterNamesAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new DifferentlyNamedOverloadedTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void DuplicateTransformedParameterNamesAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions
        {
            AllowFlexibleNamedArgumentMatching = true,
            ParameterNameTransform = _ => "value",
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new TwoParameterTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains(nameof(TwoParameterTarget.Select), exception.Message);
    }

    [Fact]
    public void DuplicateAttributedParameterNamesAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new DuplicateAttributedParameterTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains(nameof(DuplicateAttributedParameterTarget.Select), exception.Message);
    }

    [Fact]
    public void CaseInsensitiveDuplicateParameterNamesAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new CaseInsensitiveDuplicateParameterTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains(nameof(CaseInsensitiveDuplicateParameterTarget.Select), exception.Message);
    }

    [Fact]
    public void OverloadsAcrossSeparateRegistrationsAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };
        server.AddLocalRpcTarget(new IntegerTarget(), options);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new StringTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void SameTypedOverloadsWithDifferentParameterNamesAcrossSeparateRegistrationsAreRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };
        server.AddLocalRpcTarget(new FirstNamedTarget(), options);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => server.AddLocalRpcTarget(new SecondNamedTarget(), options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void LocalMethodAddedAfterFlexibleTargetIsRejected()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };
        server.AddLocalRpcTarget(new IntegerTarget(), options);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => server.AddLocalRpcMethod("Select", new Func<string, string>(value => value)));

        Assert.Contains("Select", exception.Message);
    }

    [Fact]
    public void CancellationTokenLocalMethodAddedAfterFlexibleTargetIsAllowed()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };
        server.AddLocalRpcTarget(new NonCancelableTarget(), options);
        var cancelableMethod = typeof(CancelableTarget).GetMethod(nameof(CancelableTarget.Select))!;

        server.AddLocalRpcMethod("Select", cancelableMethod, new CancelableTarget());
    }

    [Fact]
    public void NonCancellationTokenLocalMethodAddedAfterFlexibleCancelableTargetIsAllowed()
    {
        using var server = new JsonRpc(new MemoryStream());
        var options = new JsonRpcTargetOptions { AllowFlexibleNamedArgumentMatching = true };
        server.AddLocalRpcTarget(new CancelableTarget(), options);
        var nonCancelableMethod = typeof(NonCancelableTarget).GetMethod(nameof(NonCancelableTarget.Select))!;

        server.AddLocalRpcMethod("Select", nonCancelableMethod, new NonCancelableTarget());
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

    private class VirtualBaseTarget
    {
        public virtual int GetValue(int value = 3) => value;
    }

    private sealed class VirtualDerivedTarget : VirtualBaseTarget, IInterfaceDefaultTarget
    {
        public override int GetValue(int value = 5) => value;
    }

    private class BaseContractDefaultTarget
    {
        public virtual int GetValue(int value = 7) => value;
    }

    private sealed class DerivedContractDefaultTarget : BaseContractDefaultTarget
    {
        public override int GetValue(int value) => value;
    }

    private sealed class OverloadedTarget
    {
        [JsonRpcMethod("Select")]
        public string SelectExact(int value) => "exact";

        [JsonRpcMethod("Select")]
        public string SelectFlexible(int value, int missing) => "flexible";
    }

    private sealed class NonCancelableTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int value) => "non-cancelable";
    }

    private sealed class CancelableTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int value, CancellationToken cancellationToken) => "cancelable";
    }

    private sealed class DifferentlyNamedCancelableTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int second, CancellationToken cancellationToken) => "cancelable";
    }

    private sealed class MultipleCancelableTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int value) => "non-cancelable";

        [JsonRpcMethod("Select")]
        public string SelectFirst(int value, CancellationToken cancellationToken) => "first";

        [JsonRpcMethod("Select")]
        public string SelectSecond(int value, CancellationToken cancellationToken) => "second";
    }

    private sealed class StrictMultipleCancelableTarget
    {
        [JsonRpcMethod("Select")]
        public string SelectFirst(int value, CancellationToken cancellationToken) => "first";

        [JsonRpcMethod("Select")]
        public string SelectSecond(int value, CancellationToken cancellationToken) => "second";
    }

    private sealed class DifferentlyNamedOverloadedTarget
    {
        [JsonRpcMethod("Select")]
        public string SelectFirst(int first) => "first";

        [JsonRpcMethod("Select")]
        public string SelectSecond(int second) => "second";
    }

    private sealed class TwoParameterTarget
    {
        public string Select(int first, int second) => $"{first}, {second}";
    }

    private sealed class DuplicateAttributedParameterTarget
    {
        public string Select([JsonRpcParameter("value")] int first, [JsonRpcParameter("value")] int second) => $"{first}, {second}";
    }

    private sealed class CaseInsensitiveDuplicateParameterTarget
    {
        public string Select([JsonRpcParameter("value")] int first, [JsonRpcParameter("VALUE")] int second) => $"{first}, {second}";
    }

    private sealed class IntegerTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int value) => value.ToString();
    }

    private sealed class StringTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(string value) => value;
    }

    private sealed class FirstNamedTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int first) => first.ToString();
    }

    private sealed class SecondNamedTarget
    {
        [JsonRpcMethod("Select")]
        public string Select(int second) => second.ToString();
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
