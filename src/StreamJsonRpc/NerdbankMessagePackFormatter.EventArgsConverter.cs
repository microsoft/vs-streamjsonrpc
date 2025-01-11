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
    /// <summary>
    /// Enables formatting the default/empty <see cref="EventArgs"/> class.
    /// </summary>
    private class EventArgsConverter : MessagePackConverter<EventArgs>
    {
        internal static readonly EventArgsConverter Instance = new();

        private EventArgsConverter()
        {
        }

        /// <inheritdoc/>
        public override void Write(ref MessagePackWriter writer, in EventArgs? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));
            context.DepthStep();
            writer.WriteMapHeader(0);
        }

        /// <inheritdoc/>
        public override EventArgs Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();
            reader.Skip(context);
            return EventArgs.Empty;
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(EventArgsConverter));
        }
    }
}
