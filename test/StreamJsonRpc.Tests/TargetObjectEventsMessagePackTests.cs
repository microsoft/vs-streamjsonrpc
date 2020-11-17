using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using StreamJsonRpc;
using Xunit.Abstractions;

public class TargetObjectEventsMessagePackTests : TargetObjectEventsTests
{
    public TargetObjectEventsMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        var serverMessageFormatter = new MessagePackFormatter();
        var clientMessageFormatter = new MessagePackFormatter();

        var options = MessagePackFormatter.DefaultUserDataSerializationOptions
            .WithResolver(CompositeResolver.Create(
                new IMessagePackFormatter[] { },
                new IFormatterResolver[] { StandardResolverAllowPrivate.Instance }));
        serverMessageFormatter.SetMessagePackSerializerOptions(options);
        clientMessageFormatter.SetMessagePackSerializerOptions(options);

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, clientMessageFormatter);
    }
}
