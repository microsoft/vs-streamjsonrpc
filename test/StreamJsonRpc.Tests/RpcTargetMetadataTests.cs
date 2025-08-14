// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class RpcTargetMetadataTests
{
    internal interface IRpcContractBase
    {
        event EventHandler BaseEvent;

        Task MethodBaseAsync(int value);

        [JsonRpcMethod("RenamedBaseMethod2")]
        Task RenamedBaseMethod();

        [JsonRpcIgnore]
        Task IgnoredMethod();
    }

    internal interface IRpcContractDerived : IRpcContractBase
    {
        event EventHandler<MyEventArgs> DerivedEvent;

        Task MethodDerivedAsync(int value);
    }

    [Fact]
    public void FromInterface_ReturnsInheritedMembers()
    {
        Type rpcContract = typeof(IRpcContractDerived);

        RpcTargetMetadata metadata = RpcTargetMetadata.FromInterface(rpcContract);

        Assert.Contains(metadata.Methods, m => m.Key == nameof(IRpcContractBase.MethodBaseAsync));
        Assert.Contains(metadata.Events, e => e.Event.Name == nameof(IRpcContractBase.BaseEvent));
    }

    [Fact]
    public void FromInterface_ReturnsDirectMembers()
    {
        Type rpcContract = typeof(IRpcContractDerived);

        RpcTargetMetadata metadata = RpcTargetMetadata.FromInterface(rpcContract);

        Assert.Contains(metadata.Methods, m => m.Key == nameof(IRpcContractDerived.MethodDerivedAsync));
        Assert.Contains(metadata.Events, e => e.Event.Name == nameof(IRpcContractDerived.DerivedEvent));
    }

    [Fact]
    public void MethodRenameInheritedFromInterface()
    {
        RpcTargetMetadata metadata = RpcTargetMetadata.FromClass(typeof(RpcContractDerivedClass));
        Assert.Contains(metadata.Methods, m => m.Key == "RenamedBaseMethod2");
        Assert.DoesNotContain(metadata.Methods, m => m.Key == nameof(IRpcContractBase.RenamedBaseMethod));
    }

    [Fact]
    public void MethodRenameDirectlyOnClass()
    {
        RpcTargetMetadata metadata = RpcTargetMetadata.FromClass(typeof(RpcContractDerivedClass));
        Assert.Contains(metadata.Methods, m => m.Key == "RenamedDerivedMethod");
        Assert.DoesNotContain(metadata.Methods, m => m.Key == nameof(IRpcContractDerived.MethodDerivedAsync));
    }

    [Fact]
    public void IgnoredMethodByInterfaceAttribute()
    {
        RpcTargetMetadata metadata = RpcTargetMetadata.FromClass(typeof(RpcContractDerivedClass));
        Assert.DoesNotContain(metadata.Methods, m => m.Key == nameof(IRpcContractBase.IgnoredMethod));
    }

    internal class MyEventArgs : EventArgs;

    internal class RpcContractDerivedClass : IRpcContractDerived
    {
        public event EventHandler? BaseEvent;

        public event EventHandler<MyEventArgs>? DerivedEvent;

        public Task IgnoredMethod() => throw new NotImplementedException();

        public Task MethodBaseAsync(int value) => throw new NotImplementedException();

        [JsonRpcMethod("RenamedDerivedMethod")]
        public Task MethodDerivedAsync(int value) => throw new NotImplementedException();

        public Task RenamedBaseMethod() => throw new NotImplementedException();

        internal void OnBaseEvent() => this.BaseEvent?.Invoke(this, EventArgs.Empty);

        internal void OnDerivedEvent(MyEventArgs e) => this.DerivedEvent?.Invoke(this, e);
    }
}
