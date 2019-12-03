// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using Microsoft;

    internal static class Utilities
    {
        /// <summary>
        /// Gets a value indicating whether the mono runtime is executing this code.
        /// </summary>
        internal static bool IsRunningOnMono => Type.GetType("Mono.Runtime") != null;

        /// <summary>
        /// Reads an <see cref="int"/> value from a buffer using big endian.
        /// </summary>
        /// <param name="sequence">The sequence of bytes to read from. Must be at least 4 bytes long.</param>
        /// <returns>The read value.</returns>
        internal static int ReadInt32BE(ReadOnlySequence<byte> sequence)
        {
            sequence = sequence.Slice(0, 4);
            Span<byte> stackSpan = stackalloc byte[4];
            sequence.Slice(0, 4).CopyTo(stackSpan);
            return ReadIntBE(stackSpan);
        }

        /// <summary>
        /// Reads an <see cref="int"/> value to a buffer using big endian.
        /// </summary>
        /// <param name="buffer">The buffer to read from. Must be at most 4 bytes long.</param>
        /// <returns>The read value.</returns>
        internal static int ReadIntBE(ReadOnlySpan<byte> buffer)
        {
            Requires.Argument(buffer.Length <= 4, nameof(buffer), "Int32 length exceeded.");

            int local = 0;
            for (int offset = 0; offset < buffer.Length; offset++)
            {
                local <<= 8;
                local |= buffer[offset];
            }

            return local;
        }

        /// <summary>
        /// Writes an <see cref="int"/> value to a buffer using big endian.
        /// </summary>
        /// <param name="buffer">The buffer to write to. Must be at least 4 bytes long.</param>
        /// <param name="value">The value to write.</param>
        internal static void Write(Span<byte> buffer, int value)
        {
            buffer[0] = (byte)(value >> 24);
            buffer[1] = (byte)(value >> 16);
            buffer[2] = (byte)(value >> 8);
            buffer[3] = (byte)value;
        }
    }
}
