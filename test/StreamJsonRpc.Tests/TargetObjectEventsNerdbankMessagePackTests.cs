using PolyType;

public partial class TargetObjectEventsNerdbankMessagePackTests : TargetObjectEventsTests
{
    public TargetObjectEventsNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverMessageFormatter = new() { TypeShapeProvider = Witness.ShapeProvider };
        NerdbankMessagePackFormatter clientMessageFormatter = new() { TypeShapeProvider = Witness.ShapeProvider };

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);
    }

    [GenerateShape<EventArgs>]
    [GenerateShape<MessageEventArgs<string>>]
    private partial class Witness;
}
