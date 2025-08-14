using Nerdbank.Streams;
using PolyType;

namespace NativeAOT;

partial class NerdbankMessagePack
{
    #region Sample
    static async Task Main(string[] args)
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

    static IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter()
    {
        TypeShapeProvider = Witness.ShapeProvider,
    };

    [JsonRpcContract]
    internal partial interface IServer
    {
        Task<int> AddAsync(int a, int b);
    }

    class Server : IServer
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);
    }

    // Every data type used in the RPC methods must be annotated for serialization.
    [GenerateShapeFor<int>]
    private partial class Witness;
    #endregion
}
