// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc.Reflection;

public class RpcTargetMetadataTests
{
    internal interface IRpcContractBase
    {
        event EventHandler BaseEvent;

        Task MethodBaseAsync(int value);
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

    internal class MyEventArgs : EventArgs;
}
