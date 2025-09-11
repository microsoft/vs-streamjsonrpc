using PolyType;

public partial class TargetObjectEventsNerdbankMessagePackTests(ITestOutputHelper logger) : TargetObjectEventsTests(logger)
{
    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverMessageFormatter = new() { TypeShapeProvider = Witness.GeneratedTypeShapeProvider };
        NerdbankMessagePackFormatter clientMessageFormatter = new() { TypeShapeProvider = Witness.GeneratedTypeShapeProvider };

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);
    }

    [GenerateShapeFor<bool>]
    private partial class Witness;
}
