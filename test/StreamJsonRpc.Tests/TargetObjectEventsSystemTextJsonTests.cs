using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft;

public class TargetObjectEventsSystemTextJsonTests : TargetObjectEventsTests
{
    public TargetObjectEventsSystemTextJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        var clientFormatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions =
            {
                Converters =
                {
                    new IFruitConverter(),
                },
            },
        };

        var serverFormatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions =
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
        public override IFruit? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            Assumes.True(reader.Read());
            Assumes.True(reader.GetString() == nameof(IFruit.Name));
            Assumes.True(reader.Read());
            string? name = reader.GetString();
            return new Fruit(name ?? throw new JsonException("Unexpected null."));
        }

        public override void Write(Utf8JsonWriter writer, IFruit? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartObject();
            writer.WriteString(nameof(IFruit.Name), value.Name);
            writer.WriteEndObject();
        }
    }
}
