using PolyType;

public partial class WebSocketMessageHandlerNerdbankMessagePackTests : WebSocketMessageHandlerTests
{
    public WebSocketMessageHandlerNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(new NerdbankMessagePackFormatter { TypeShapeProvider = Witness.ShapeProvider }, logger)
    {
    }

    [GenerateShapeFor<int>]
    private partial class Witness;
}
