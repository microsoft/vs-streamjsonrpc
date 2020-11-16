using StreamJsonRpc;
using Xunit.Abstractions;

public class TargetObjectEventsJsonTests : TargetObjectEventsTests
{
    public TargetObjectEventsJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        var clientFormatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                Converters =
                {
                },
            },
        };

        var serverFormatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                Converters =
                {
                },
            },
        };

        this.serverMessageHandler = new HeaderDelimitedMessageHandler(this.serverStream, serverFormatter);
        this.clientMessageHandler = new HeaderDelimitedMessageHandler(this.clientStream, clientFormatter);
    }
}
