// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name

using NativeAOTCompatibility.Test;
using StreamJsonRpc;

Console.WriteLine("This test is run by \"dotnet publish -r [RID]-x64\" rather than by executing the program.");

// That said, this "program" can run select scenarios to verify that they work in a Native AOT environment.
// When TUnit fixes https://github.com/thomhurst/TUnit/issues/2458, we can move this part of the program to unit tests.
await NerdbankMessagePack.RunAsync();
await SystemTextJson.RunAsync();

[JsonRpcContract]
internal partial interface IServer
{
    event EventHandler<int> Added;

    Task<int> AddAsync(int a, int b);
}

internal class Server : IServer
{
    public event EventHandler<int>? Added;

    public Task<int> AddAsync(int a, int b)
    {
        int sum = a + b;
        this.OnAdded(sum);
        return Task.FromResult(sum);
    }

    protected virtual void OnAdded(int sum) => this.Added?.Invoke(this, sum);
}
