using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nerdbank.Streams;

namespace NativeAOT;

partial class Program
{
    #region STJSample
    static async Task Main(string[] args)
    {
        (Stream clientPipe, Stream serverPipe) = FullDuplexStream.CreatePair();
        JsonRpc serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverPipe, CreateFormatter()));
        JsonRpc clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(clientPipe, CreateFormatter()));
        serverRpc.AddLocalRpcMethod(nameof(Server.AddAsync), new Server().AddAsync);
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

    [RpcContract]
    internal interface IServer
    {
        Task<int> AddAsync(int a, int b);
    }

    class Server : IServer
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);
    }
    #endregion
}
