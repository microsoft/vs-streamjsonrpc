using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nerdbank.Streams;
using StreamJsonRpc.Reflection;

namespace NativeAOT;

partial class Program
{
    #region STJSample
    static async Task Main(string[] args)
    {
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
    }

    // When properly configured, this formatter is safe in Native AOT scenarios for
    // the very limited use case shown in this program.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Using the Json source generator.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Using the Json source generator.")]
    static IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter()
    {
        JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext.Default },
    };

    // Every data type used in the RPC methods must be annotated for serialization.
    [JsonSerializable(typeof(int))]
    partial class SourceGenerationContext : JsonSerializerContext;

    [JsonRpcContract]
    internal partial interface IServer
    {
        Task<int> AddAsync(int a, int b);
    }

    class Server : IServer
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);
    }
    #endregion
}
