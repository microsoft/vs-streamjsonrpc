// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using NBMP = Nerdbank.MessagePack;

namespace StreamJsonRpc;

public partial class NerdbankMessagePackFormatter
{
    [DebuggerDisplay("{" + nameof(Value) + "}")]
    private struct CommonString
    {
        internal CommonString(string value)
        {
            Requires.Argument(value.Length > 0 && value.Length <= 16, nameof(value), "Length must be >0 and <=16.");
            this.Value = value;
            ReadOnlyMemory<byte> encodedBytes = MessagePack.Internal.CodeGenHelpers.GetEncodedStringBytes(value);
            this.EncodedBytes = encodedBytes;

            ReadOnlySpan<byte> span = this.EncodedBytes.Span.Slice(1);
            this.Key = MessagePack.Internal.AutomataKeyGen.GetKey(ref span); // header is 1 byte because string length <= 16
            this.Key2 = span.Length > 0 ? (ulong?)MessagePack.Internal.AutomataKeyGen.GetKey(ref span) : null;
        }

        /// <summary>
        /// Gets the original string.
        /// </summary>
        internal string Value { get; }

        /// <summary>
        /// Gets the 64-bit integer that represents the string without decoding it.
        /// </summary>
        private ulong Key { get; }

        /// <summary>
        /// Gets the next 64-bit integer that represents the string without decoding it.
        /// </summary>
        private ulong? Key2 { get; }

        /// <summary>
        /// Gets the messagepack header and UTF-8 bytes for this string.
        /// </summary>
        private ReadOnlyMemory<byte> EncodedBytes { get; }

        /// <summary>
        /// Writes out the messagepack binary for this common string, if it matches the given value.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        /// <param name="value">The value to be written, if it matches this <see cref="CommonString"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="value"/> matches this <see cref="Value"/> and it was written; <see langword="false"/> otherwise.</returns>
        internal bool TryWrite(ref NBMP::MessagePackWriter writer, string value)
        {
            if (value == this.Value)
            {
                this.Write(ref writer);
                return true;
            }

            return false;
        }

        internal readonly void Write(ref NBMP::MessagePackWriter writer) => writer.WriteRaw(this.EncodedBytes.Span);

        /// <summary>
        /// Checks whether a span of UTF-8 bytes equal this common string.
        /// </summary>
        /// <param name="utf8String">The UTF-8 string.</param>
        /// <returns><see langword="true"/> if the UTF-8 bytes are the encoding of this common string; <see langword="false"/> otherwise.</returns>
        internal readonly bool TryRead(ReadOnlySpan<byte> utf8String)
        {
            if (utf8String.Length != this.EncodedBytes.Length - 1)
            {
                return false;
            }

            ulong key1 = MessagePack.Internal.AutomataKeyGen.GetKey(ref utf8String);
            if (key1 != this.Key)
            {
                return false;
            }

            if (utf8String.Length > 0)
            {
                if (!this.Key2.HasValue)
                {
                    return false;
                }

                ulong key2 = MessagePack.Internal.AutomataKeyGen.GetKey(ref utf8String);
                if (key2 != this.Key2.Value)
                {
                    return false;
                }
            }
            else if (this.Key2.HasValue)
            {
                return false;
            }

            return true;
        }
    }
}
