// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Newtonsoft.Json;

#pragma warning disable CA1812
    internal class RequestIdJsonConverter : JsonConverter<RequestId>
#pragma warning restore CA1812
    {
        public override RequestId ReadJson(JsonReader reader, Type objectType, RequestId existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Integer: return new RequestId(reader.Value is int i ? i : (long)reader.Value);
                case JsonToken.String: return new RequestId((string)reader.Value);
                case JsonToken.Null: return RequestId.Null;
                default: throw new JsonSerializationException("Unexpected token type for request ID: " + reader.TokenType);
            }
        }

        public override void WriteJson(JsonWriter writer, RequestId value, JsonSerializer serializer)
        {
            if (value.Number.HasValue)
            {
                writer.WriteValue(value.Number.Value);
            }
            else
            {
                writer.WriteValue(value.String);
            }
        }
    }
}
