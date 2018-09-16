// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Text;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An <see cref="IJsonRpcMessageFormatter"/> that formats messages as JSON (text).
    /// </summary>
    public interface IJsonRpcMessageTextFormatter : IJsonRpcMessageFormatter
    {
        /// <summary>
        /// Gets or sets the encoding used for serialization for methods that do not take an explicit <see cref="System.Text.Encoding"/>.
        /// </summary>
        /// <value>Never null.</value>
        /// <exception cref="ArgumentNullException">Thrown at an attempt to set the value to null.</exception>
        Encoding Encoding { get; set; }

        /// <summary>
        /// Deserializes a sequence of bytes to a <see cref="JsonRpcMessage"/>.
        /// </summary>
        /// <param name="contentBuffer">The bytes to deserialize.</param>
        /// <param name="encoding">The encoding to read the bytes in <paramref name="contentBuffer"/> with.</param>
        /// <returns>The deserialized message.</returns>
        JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding);
    }
}
