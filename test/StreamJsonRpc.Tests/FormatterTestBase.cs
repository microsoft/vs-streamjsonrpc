using System.Runtime.Serialization;
using Nerdbank.Streams;

public abstract class FormatterTestBase<TFormatter> : TestBase
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

    [SkippableFact]
    public void TopLevelPropertiesCanBeSerializedRequest()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Skip.If(factory is null);
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

    [SkippableFact]
    public void TopLevelPropertiesCanBeSerializedResult()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Skip.If(factory is null);
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

    [SkippableFact]
    public void TopLevelPropertiesCanBeSerializedError()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Skip.If(factory is null);
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

    [SkippableFact]
    public void TopLevelPropertiesWithNullValue()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Skip.If(factory is null);
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

    [DataContract]
    public class CustomType
    {
        [DataMember]
        public int Age { get; set; }
    }
}
