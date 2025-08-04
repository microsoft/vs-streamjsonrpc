// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Reflection;

Console.WriteLine("This test is run by \"dotnet publish -r [RID]-x64\" rather than by executing the program.");

// That said, this "program" can run select scenarios to verify that they work in a Native AOT environment.
// When TUnit fixes https://github.com/thomhurst/TUnit/issues/2458, we can move this part of the program to unit tests.
(Stream clientPipe, Stream serverPipe) = FullDuplexStream.CreatePair();
JsonRpc serverRpc = new(new HeaderDelimitedMessageHandler(serverPipe, CreateFormatter()));
JsonRpc clientRpc = new(new HeaderDelimitedMessageHandler(clientPipe, CreateFormatter()));
serverRpc.AddLocalRpcTarget(
    RpcTargetMetadata.FromInterface(new RpcTargetMetadata.InterfaceCollection(typeof(IServer))),
    new Server(),
    null);
serverRpc.StartListening();
IServer proxy = clientRpc.Attach<IServer>();
clientRpc.StartListening();

int sum = await proxy.AddAsync(2, 5);
Console.WriteLine($"2 + 5 = {sum}");

// When properly configured, this formatter is safe in Native AOT scenarios for
// the very limited use case shown in this program.
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Using the Json source generator.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Using the Json source generator.")]
IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter()
{
    JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext.Default },
};

[JsonRpcContract]
internal partial interface IServer
{
    Task<int> AddAsync(int a, int b);
}

internal class Server : IServer
{
    public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);
}

[JsonSerializable(typeof(int))]
internal partial class SourceGenerationContext : JsonSerializerContext;
