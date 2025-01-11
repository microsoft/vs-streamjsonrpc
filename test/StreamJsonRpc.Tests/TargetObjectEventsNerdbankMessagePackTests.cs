public class TargetObjectEventsNerdbankMessagePackTests : TargetObjectEventsTests
{
    public TargetObjectEventsNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        var serverMessageFormatter = new NerdbankMessagePackFormatter();
        var clientMessageFormatter = new NerdbankMessagePackFormatter();

        serverMessageFormatter.SetFormatterProfile(ConfigureContext);
        clientMessageFormatter.SetFormatterProfile(ConfigureContext);

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);

        void ConfigureContext(NerdbankMessagePackFormatter.Profile.Builder profileBuilder)
        {
            profileBuilder.AddTypeShapeProvider(PolyType.SourceGenerator.ShapeProvider_StreamJsonRpc_Tests.Default);
            profileBuilder.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        }
    }
}
