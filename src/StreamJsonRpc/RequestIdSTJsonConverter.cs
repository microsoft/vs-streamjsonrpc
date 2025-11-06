// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamJsonRpc;

/// <summary>
/// A System.Text.Json converter for <see cref="RequestId"/>.
/// </summary>
internal class RequestIdSTJsonConverter : JsonConverter<RequestId>
{
    /// <summary>
    /// A singleton that can be used to reduce allocations.
    /// </summary>
    internal static readonly RequestIdSTJsonConverter Instance = new();

    /// <inheritdoc/>
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => new RequestId(reader.GetInt64()),
            JsonTokenType.String => new RequestId(reader.GetString()),
            JsonTokenType.Null => RequestId.Null,
            _ => throw new JsonException("Unexpected token type for id property: " + reader.TokenType),
        };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.Number is long idNumber)
        {
            writer.WriteNumberValue(idNumber);
        }
        else if (value.String is string idString)
        {
            writer.WriteStringValue(idString);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
