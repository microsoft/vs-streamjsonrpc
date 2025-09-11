// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;

public partial class JsonRpcNerdbankMessagePackLengthTests(ITestOutputHelper logger) : JsonRpcMessagePackLengthTests(logger)
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
        NerdbankMessagePackFormatter serverFormatter = new()
        {
            TypeShapeProvider = Witness.GeneratedTypeShapeProvider,
            UserDataSerializer = NerdbankMessagePackFormatter.DefaultSerializer with
            {
                Converters =
                [
                    ..NerdbankMessagePackFormatter.DefaultSerializer.Converters,
                    new CustomExtensionConverter(),
                    new UnserializableTypeConverter(),
                    new TypeThrowsWhenDeserializedConverter(),
                ],
            },
        };

        NerdbankMessagePackFormatter clientFormatter = new()
        {
            TypeShapeProvider = Witness.GeneratedTypeShapeProvider,
            UserDataSerializer = serverFormatter.UserDataSerializer,
        };

        serverMessageFormatter = serverFormatter;
        clientMessageFormatter = clientFormatter;

        serverMessageHandler = new LengthHeaderMessageHandler(serverStream, serverStream, serverMessageFormatter);
        clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(clientStream, clientMessageFormatter)
            : new LengthHeaderMessageHandler(clientStream, clientStream, clientMessageFormatter);
    }

    protected override object[] CreateFormatterIntrinsicParamsObject(string arg) => [];

    internal class CustomExtensionConverter : MessagePackConverter<CustomExtensionType>
    {
        public override CustomExtensionType? Read(ref MessagePackReader reader, SerializationContext context)
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            if (reader.ReadExtensionHeader() is { TypeCode: 1, Length: 0 })
            {
                return new();
            }

            throw new Exception("Unexpected extension header.");
        }

        public override void Write(ref MessagePackWriter writer, in CustomExtensionType? value, SerializationContext context)
        {
            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                writer.Write(new Extension(1, default(Memory<byte>)));
            }
        }
    }

    private class UnserializableTypeConverter : MessagePackConverter<CustomSerializedType>
    {
        public override CustomSerializedType Read(ref MessagePackReader reader, SerializationContext context)
        {
            return new CustomSerializedType { Value = reader.ReadString() };
        }

        public override void Write(ref MessagePackWriter writer, in CustomSerializedType? value, SerializationContext context)
        {
            writer.Write(value?.Value);
        }
    }

    private class TypeThrowsWhenDeserializedConverter : MessagePackConverter<TypeThrowsWhenDeserialized>
    {
        public override TypeThrowsWhenDeserialized Read(ref MessagePackReader reader, SerializationContext context)
        {
            throw CreateExceptionToBeThrownByDeserializer();
        }

        public override void Write(ref MessagePackWriter writer, in TypeThrowsWhenDeserialized? value, SerializationContext context)
        {
            writer.WriteArrayHeader(0);
        }
    }

    [GenerateShapeFor<bool>]
    private partial class Witness;
}
