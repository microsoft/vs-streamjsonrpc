using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nerdbank.Streams;

namespace NativeAOT;

partial class SystemTextJson
{
    #region Sample
    static async Task Main(string[] args)
    {
        (Stream clientPipe, Stream serverPipe) = FullDuplexStream.CreatePair();
        JsonRpc serverRpc = new(new HeaderDelimitedMessageHandler(serverPipe, CreateFormatter()));
        JsonRpc clientRpc = new(new HeaderDelimitedMessageHandler(clientPipe, CreateFormatter()));

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
    static IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter()
    {
        JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext.Default },
    };

    // Every data type used in the RPC methods must be annotated for serialization.
    [JsonSerializable(typeof(int))]
    partial class SourceGenerationContext : JsonSerializerContext;

    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    internal partial interface IServer
    {
        event EventHandler<int> Added;

        Task<int> AddAsync(int a, int b);
    }

    class Server : IServer
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
    #endregion
}
