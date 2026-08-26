using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Reflection;

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

        await foreach (CommandOutput output in proxy.GetOutputsAsync())
        {
            Console.WriteLine(output.Text);
        }
    }

    // When properly configured, this formatter is safe in Native AOT scenarios for
    // the very limited use case shown in this program.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Using the Json source generator.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Using the Json source generator.")]
    private static IJsonRpcMessageFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext.Default },
        };
        formatter.RegisterGenericType<CommandOutput>();
        return formatter;
    }

    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(IAsyncEnumerable<CommandOutput>))]
    [JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<CommandOutput>))]
    private partial class SourceGenerationContext : JsonSerializerContext;
}
