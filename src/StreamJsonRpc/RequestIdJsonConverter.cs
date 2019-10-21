// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Newtonsoft.Json;

    internal class RequestIdJsonConverter : JsonConverter<RequestId>
    {
        public override RequestId ReadJson(JsonReader reader, Type objectType, RequestId existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Integer: return new RequestId(reader.Value is int i ? i : (long)reader.Value);
                case JsonToken.String: return new RequestId((string)reader.Value);
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
