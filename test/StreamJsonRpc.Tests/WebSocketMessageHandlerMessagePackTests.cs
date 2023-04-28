public class WebSocketMessageHandlerMessagePackTests : WebSocketMessageHandlerTests
{
    public WebSocketMessageHandlerMessagePackTests(ITestOutputHelper logger)
        : base(new MessagePackFormatter(), logger)
    {
    }
}
