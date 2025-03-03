// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace StreamJsonRpc;

internal static class Utilities
{
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
        return BinaryPrimitives.ReadInt32BigEndian(stackSpan);
    }

    /// <summary>
    /// Copies a <see cref="ReadOnlySequence{T}"/> to an <see cref="IBufferWriter{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of element to copy.</typeparam>
    /// <param name="sequence">The sequence to read from.</param>
    /// <param name="writer">The target to write to.</param>
    internal static void CopyTo<T>(this in ReadOnlySequence<T> sequence, IBufferWriter<T> writer)
    {
        Requires.NotNull(writer, nameof(writer));

        foreach (ReadOnlyMemory<T> segment in sequence)
        {
            writer.Write(segment.Span);
        }
    }
}
