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

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);
    }
}
