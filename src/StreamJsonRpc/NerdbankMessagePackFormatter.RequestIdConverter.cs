// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private class RequestIdConverter : MessagePackConverter<RequestId>
    {
        internal static readonly RequestIdConverter Instance = new();

        private RequestIdConverter()
        {
        }

        public override RequestId Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            if (reader.NextMessagePackType == MessagePackType.Integer)
            {
                return new RequestId(reader.ReadInt64());
            }
            else
            {
                // Do *not* read as an interned string here because this ID should be unique.
                return new RequestId(reader.ReadString());
            }
        }

        public override void Write(ref MessagePackWriter writer, in RequestId value, SerializationContext context)
        {
            context.DepthStep();

            if (value.Number.HasValue)
            {
                writer.Write(value.Number.Value);
            }
            else
            {
                writer.Write(value.String);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => JsonNode.Parse("""
        {
            "type": ["string", { "type": "integer", "format": "int64" }]
        }
        """)?.AsObject();
    }
}
