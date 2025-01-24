public class WebSocketMessageHandlerNerdbankMessagePackTests : WebSocketMessageHandlerTests
{
    public WebSocketMessageHandlerNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(new NerdbankMessagePackFormatter(), logger)
    {
    }
}
