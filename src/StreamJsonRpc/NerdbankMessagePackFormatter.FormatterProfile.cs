// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public sealed partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FormatterProfile"/> class.
    /// </summary>
    /// <param name="serializer">The MessagePack serializer to use.</param>
    /// <param name="shapeProvider">The type shape provider to use.</param>
    public class FormatterProfile(MessagePackSerializer serializer, ITypeShapeProvider shapeProvider)
    {
        /// <summary>
        /// Gets the MessagePack serializer.
        /// </summary>
        internal MessagePackSerializer Serializer => serializer;

        /// <summary>
        /// Gets the type shape provider.
        /// </summary>
        internal ITypeShapeProvider ShapeProvider => shapeProvider;
    }
}
