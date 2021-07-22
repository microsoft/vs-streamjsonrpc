using System;
using Microsoft;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit.Abstractions;

public class TargetObjectEventsJsonTests : TargetObjectEventsTests
{
    public TargetObjectEventsJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        var clientFormatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                Converters =
                {
                    new IFruitConverter(),
                },
            },
        };

        var serverFormatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                Converters =
                {
                    new IFruitConverter(),
                },
            },
        };

        this.serverMessageHandler = new HeaderDelimitedMessageHandler(this.serverStream, serverFormatter);
        this.clientMessageHandler = new HeaderDelimitedMessageHandler(this.clientStream, clientFormatter);
    }

    private class IFruitConverter : JsonConverter<IFruit?>
    {
        public override IFruit? ReadJson(JsonReader reader, Type objectType, IFruit? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            Assumes.True(reader.Read());
            Assumes.True((string?)reader.Value == nameof(IFruit.Name));
            Assumes.True(reader.Read());
            string name = (string)reader.Value;
            return new Fruit(name);
        }

        public override void WriteJson(JsonWriter writer, IFruit? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName(nameof(IFruit.Name));
            writer.WriteValue(value.Name);
            writer.WriteEndObject();
        }
    }
}
