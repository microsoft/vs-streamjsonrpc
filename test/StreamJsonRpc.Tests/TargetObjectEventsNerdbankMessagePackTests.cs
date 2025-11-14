public partial class TargetObjectEventsNerdbankMessagePackTests(ITestOutputHelper logger) : TargetObjectEventsTests(logger)
{
    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverMessageFormatter = new() { TypeShapeProvider = Witness.GeneratedTypeShapeProvider };
        NerdbankMessagePackFormatter clientMessageFormatter = new() { TypeShapeProvider = Witness.GeneratedTypeShapeProvider };

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);
    }

    protected override JsonRpc CreateJsonRpcWithTargetObject<T>(IJsonRpcMessageHandler messageHandler, T targetObject, JsonRpcTargetOptions? options)
    {
        JsonRpc jsonRpc = new(messageHandler);
        jsonRpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape(Witness.GeneratedTypeShapeProvider.GetTypeShapeOrThrow<T>()), targetObject, options);
        return jsonRpc;
    }

    [GenerateShapeFor<Server>(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [GenerateShapeFor<Client>(IncludeMethods = MethodShapeFlags.PublicInstance)]
    private partial class Witness;
}
