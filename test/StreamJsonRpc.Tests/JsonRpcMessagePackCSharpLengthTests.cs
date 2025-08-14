// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

public class JsonRpcMessagePackCSharpLengthTests(ITestOutputHelper logger) : JsonRpcMessagePackLengthTests(logger)
{
    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override void InitializeFormattersAndHandlers(
        Stream serverStream,
        Stream clientStream,
        out IJsonRpcMessageFormatter serverMessageFormatter,
        out IJsonRpcMessageFormatter clientMessageFormatter,
        out IJsonRpcMessageHandler serverMessageHandler,
        out IJsonRpcMessageHandler clientMessageHandler,
        bool controlledFlushingClient)
    {
        serverMessageFormatter = new MessagePackFormatter();
        clientMessageFormatter = new MessagePackFormatter();

        var options = MessagePackFormatter.DefaultUserDataSerializationOptions
            .WithResolver(CompositeResolver.Create(
                new IMessagePackFormatter[] { new UnserializableTypeFormatter(), new TypeThrowsWhenDeserializedFormatter(), new CustomExtensionFormatter() },
                new IFormatterResolver[] { StandardResolverAllowPrivate.Instance }));
        ((MessagePackFormatter)serverMessageFormatter).SetMessagePackSerializerOptions(options);
        ((MessagePackFormatter)clientMessageFormatter).SetMessagePackSerializerOptions(options);

        serverMessageHandler = new LengthHeaderMessageHandler(serverStream, serverStream, serverMessageFormatter);
        clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(clientStream, clientMessageFormatter)
            : new LengthHeaderMessageHandler(clientStream, clientStream, clientMessageFormatter);
    }

    private class CustomExtensionFormatter : IMessagePackFormatter<CustomExtensionType?>
    {
        public CustomExtensionType? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            if (reader.ReadExtensionFormat() is { Header: { TypeCode: 1, Length: 0 } })
            {
                return new();
            }

            throw new Exception("Unexpected extension header.");
        }

        public void Serialize(ref MessagePackWriter writer, CustomExtensionType? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                writer.WriteExtensionFormat(new ExtensionResult(1, default(Memory<byte>)));
            }
        }
    }

    private class UnserializableTypeFormatter : IMessagePackFormatter<CustomSerializedType>
    {
        public CustomSerializedType Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return new CustomSerializedType { Value = reader.ReadString() };
        }

        public void Serialize(ref MessagePackWriter writer, CustomSerializedType value, MessagePackSerializerOptions options)
        {
            writer.Write(value?.Value);
        }
    }

    private class TypeThrowsWhenDeserializedFormatter : IMessagePackFormatter<TypeThrowsWhenDeserialized>
    {
        public TypeThrowsWhenDeserialized Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            throw CreateExceptionToBeThrownByDeserializer();
        }

        public void Serialize(ref MessagePackWriter writer, TypeThrowsWhenDeserialized value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(0);
        }
    }
}
