using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace NativeAOTCompatibility.Test;

internal static partial class SystemTextJson
{
    internal static async Task RunAsync()
    {
        VerifyRequestIdMetadata();

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

    private static void VerifyRequestIdMetadata()
    {
        IJsonRpcMessageFormatter formatter = CreateFormatter();
        (RequestId Id, string Json)[] cases =
        [
            (new RequestId(long.MinValue), "-9223372036854775808"),
            (new RequestId("request"), "\"request\""),
            (RequestId.Null, "null"),
        ];

        foreach ((RequestId id, string expectedJson) in cases)
        {
            JsonRpcRequest request = new()
            {
                Method = "$/cancelRequest",
                Arguments = new Dictionary<string, object?> { ["id"] = id },
            };
            using Sequence<byte> sequence = new();
            formatter.Serialize(sequence, request);
            using JsonDocument document = JsonDocument.Parse(sequence);
            if (document.RootElement.GetProperty("params").GetProperty("id").GetRawText() != expectedJson)
            {
                throw new InvalidOperationException("Unexpected cancellation request ID representation.");
            }

            JsonRpcRequest deserialized = (JsonRpcRequest)formatter.Deserialize(sequence);
            if (!deserialized.TryGetArgumentByNameOrIndex("id", 0, typeof(RequestId), out object? value) || value is not RequestId actual || actual != id)
            {
                throw new InvalidOperationException("Built-in RequestId metadata was not available to the formatter.");
            }
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
            JsonSerializerOptions = SourceGenerationContext.Default.Options,
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
