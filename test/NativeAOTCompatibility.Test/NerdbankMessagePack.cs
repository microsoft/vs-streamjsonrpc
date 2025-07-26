// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.Streams;
using PolyType;
using StreamJsonRpc;

namespace NativeAOTCompatibility.Test;

internal static partial class NerdbankMessagePack
{
    internal static async Task RunAsync()
    {
        (Stream clientPipe, Stream serverPipe) = FullDuplexStream.CreatePair();
        JsonRpc serverRpc = new JsonRpc(new LengthHeaderMessageHandler(serverPipe, serverPipe, CreateFormatter()));
        JsonRpc clientRpc = new JsonRpc(new LengthHeaderMessageHandler(clientPipe, clientPipe, CreateFormatter()));
        serverRpc.AddLocalRpcMethod(nameof(Server.AddAsync), new Server().AddAsync);
        serverRpc.StartListening();
        IServer proxy = clientRpc.Attach<IServer>();
        clientRpc.StartListening();

        int sum = await proxy.AddAsync(2, 5);
        Console.WriteLine($"2 + 5 = {sum}");
    }

    private static IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter()
    {
        TypeShapeProvider = Witness.ShapeProvider,
    };

    [GenerateShapeFor<int>]
    private partial class Witness;
}
