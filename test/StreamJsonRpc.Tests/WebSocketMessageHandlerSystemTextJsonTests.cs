public class WebSocketMessageHandlerSystemTextJsonTests : WebSocketMessageHandlerTests
{
    public WebSocketMessageHandlerSystemTextJsonTests(ITestOutputHelper logger)
        : base(new SystemTextJsonFormatter(), logger)
    {
    }
}
