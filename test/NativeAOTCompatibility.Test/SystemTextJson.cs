using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace NativeAOTCompatibility.Test;

internal static partial class SystemTextJson
{
    internal static async Task RunAsync()
    {
        (Stream clientPipe, Stream serverPipe) = FullDuplexStream.CreatePair();
        JsonRpc serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverPipe, CreateFormatter()));
        JsonRpc clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientPipe, CreateFormatter()));

        var targetMetadata = RpcTargetMetadata.FromShape<IServer>();
        serverRpc.AddLocalRpcTarget(targetMetadata, new Server(), null);

        serverRpc.StartListening();
        IServer proxy = clientRpc.Attach<IServer>();
        clientRpc.StartListening();

        int sum = await proxy.AddAsync(2, 5);
        Console.WriteLine($"2 + 5 = {sum}");
    }

    // When properly configured, this formatter is safe in Native AOT scenarios for
    // the very limited use case shown in this program.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Using the Json source generator.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Using the Json source generator.")]
    private static IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter()
    {
        JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext.Default },
    };

    [JsonSerializable(typeof(int))]
    private partial class SourceGenerationContext : JsonSerializerContext;
}
