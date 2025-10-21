using PolyType;

public partial class WebSocketMessageHandlerNerdbankMessagePackTests : WebSocketMessageHandlerTests
{
    public WebSocketMessageHandlerNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(new NerdbankMessagePackFormatter { TypeShapeProvider = Witness.GeneratedTypeShapeProvider }, logger)
    {
    }

    [GenerateShapeFor<bool>]
    private partial class Witness;
}
