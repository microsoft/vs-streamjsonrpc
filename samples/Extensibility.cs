using System.IO.Pipelines;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Xunit;

public partial class Extensibility
{
    #region RpcContract
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IWeatherService
    {
        Task<int> GetTemperatureAsync(string city, CancellationToken cancellationToken);
    }
    #endregion

    #region ServiceImpl
    class WeatherService : IWeatherService
    {
        public Task<int> GetTemperatureAsync(string city, CancellationToken cancellationToken)
        {
            return Task.FromResult(city switch
            {
                "Seattle" => 55,
                "Denver" => 65,
                _ => throw new ArgumentException($"Unknown city: {city}"),
            });
        }
    }
    #endregion

    #region PolyTypeWitness
    [GenerateShapeFor<int>]
    partial class Witness;
    #endregion

    [Fact]
    public async Task ValidateNBMsgPackSample()
    {
        (IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
        await Task.WhenAll(
            this.NBMsgPackClient(clientPipe, TestContext.Current.CancellationToken),
            this.NBMsgPackServer(serverPipe, TestContext.Current.CancellationToken))
            .WithCancellation(TestContext.Current.CancellationToken);
    }

    IJsonRpcMessageFormatter CreateNBMsgPackFormatter() =>
    #region CreateNBMsgPackFormatter
        new NerdbankMessagePackFormatter
        {
            TypeShapeProvider = Witness.GeneratedTypeShapeProvider,
            UserDataSerializer = NerdbankMessagePackFormatter.DefaultSerializer with
            {
                PerfOverSchemaStability = true,
            },
        };
    #endregion

    async Task NBMsgPackClient(IDuplexPipe pipe, CancellationToken cancellationToken)
    {
        #region NBMsgPackClient
        IWeatherService proxy = JsonRpc.Attach<IWeatherService>(
            new LengthHeaderMessageHandler(
                pipe,
                this.CreateNBMsgPackFormatter()));
        using (proxy as IDisposable)
        {
            int temperature = await proxy.GetTemperatureAsync("Denver", cancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"Temp: {temperature}");
        }
        #endregion
    }

    async Task NBMsgPackServer(IDuplexPipe pipe, CancellationToken cancellationToken)
    {
        #region NBMsgPackServer
        JsonRpc rpc = new(
            new LengthHeaderMessageHandler(
                pipe,
                this.CreateNBMsgPackFormatter()));
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IWeatherService>(), new WeatherService(), options: null);
        rpc.StartListening();
        await rpc.Completion;
        #endregion
    }
}
