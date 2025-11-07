#pragma warning disable SA1402 // File may only contain a single type

using System.Runtime.Serialization;
using Nerdbank.Streams;

public abstract class FormatterTestBase : TestBase
{
    protected FormatterTestBase(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [DataContract]
    public class CustomType
    {
        [DataMember]
        public int Age { get; set; }
    }
}

public abstract class FormatterTestBase<TFormatter> : FormatterTestBase
    where TFormatter : IJsonRpcMessageFormatter
{
    private TFormatter? formatter;

    protected FormatterTestBase(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected TFormatter Formatter
    {
        get => this.formatter ??= this.CreateFormatter();
        set => this.formatter = value;
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedRequest()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        JsonRpcRequest requestMessage = factory.CreateRequestMessage();
        Assert.NotNull(requestMessage);

        requestMessage.Method = "test";
        Assert.True(requestMessage.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(requestMessage.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        JsonRpcRequest roundTripMessage = this.Roundtrip(requestMessage);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(roundTripMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedResult()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        var message = factory.CreateResultMessage();
        Assert.NotNull(message);

        message.Result = "test";
        Assert.True(message.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(message.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        var roundTripMessage = this.Roundtrip(message);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(roundTripMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedError()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        var message = factory.CreateErrorMessage();
        Assert.NotNull(message);

        message.Error = new JsonRpcError.ErrorDetail() { Message = "test" };
        Assert.True(message.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(message.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        var roundTripMessage = this.Roundtrip(message);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(roundTripMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesWithNullValue()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        var requestMessage = factory.CreateRequestMessage();
        Assert.NotNull(requestMessage);

        requestMessage.Method = "test";
        Assert.True(requestMessage.TrySetTopLevelProperty<string?>("testProperty", null));

        var roundTripMessage = this.Roundtrip(requestMessage);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Null(value);
    }

    protected abstract TFormatter CreateFormatter();

    protected T Roundtrip<T>(T value)
        where T : JsonRpcMessage
    {
        var sequence = new Sequence<byte>();
        this.Formatter.Serialize(sequence, value);
        var actual = (T)this.Formatter.Deserialize(sequence);
        return actual;
    }
}
