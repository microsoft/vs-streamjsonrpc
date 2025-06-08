// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name

using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc;

Console.WriteLine("This test is run by \"dotnet publish -r [RID]-x64\" rather than by executing the program.");

// That said, this "program" can run select scenarios to verify that they work in a Native AOT environment.
// When TUnit fixes https://github.com/thomhurst/TUnit/issues/2458, we can move this part of the program to unit tests.
(Stream clientPipe, Stream serverPipe) = FullDuplexStream.CreatePair();
JsonRpc serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverPipe, CreateFormatter()));
JsonRpc clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientPipe, CreateFormatter()));
serverRpc.AddLocalRpcMethod("Add", new Server().Add);
serverRpc.StartListening();
clientRpc.StartListening();

int sum = await clientRpc.InvokeAsync<int>(nameof(Server.Add), 2, 5);
Console.WriteLine($"2 + 5 = {sum}");

IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter()
{
    JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext.Default },
};

internal class Server
{
    public int Add(int a, int b) => a + b;
}

[JsonSerializable(typeof(int))]
internal partial class SourceGenerationContext : JsonSerializerContext;
