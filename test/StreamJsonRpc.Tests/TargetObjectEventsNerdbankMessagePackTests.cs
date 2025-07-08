using PolyType;

public partial class TargetObjectEventsNerdbankMessagePackTests(ITestOutputHelper logger) : TargetObjectEventsTests(logger)
{
    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverMessageFormatter = new() { TypeShapeProvider = Witness.ShapeProvider };
        NerdbankMessagePackFormatter clientMessageFormatter = new() { TypeShapeProvider = Witness.ShapeProvider };

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);
    }

    [GenerateShapeFor<EventArgs>]
    [GenerateShapeFor<MessageEventArgs<string>>]
    private partial class Witness;
}
