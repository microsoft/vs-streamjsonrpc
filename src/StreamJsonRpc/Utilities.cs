﻿// Copyright (c) Microsoft Corporation. All rights reserved.
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

    /// <summary>
    /// Converts a PascalCase identifier to camelCase.
    /// </summary>
    /// <param name="identifier">The identifier to convert to camcelCase.</param>
    /// <returns>The camelCase identifier. Null if <paramref name="identifier"/> is null.</returns>
    /// <devremarks>
    /// Originally taken from <see href="https://github.com/JamesNK/Newtonsoft.Json/blob/666d9760719e5ec5b2a50046f7dbd6a1267c01c6/Src/Newtonsoft.Json/Utilities/StringUtils.cs#L155-L194">Newtonsoft.Json</see>.
    /// </devremarks>
    [return: NotNullIfNotNull("identifier")]
    internal static string? ToCamelCase(string? identifier)
    {
        if (identifier is null || identifier.Length == 0 || !char.IsUpper(identifier[0]))
        {
            return identifier;
        }

        char[] chars = ArrayPool<char>.Shared.Rent(identifier.Length);
        identifier.CopyTo(0, chars, 0, identifier.Length);
        try
        {
            for (int i = 0; i < identifier.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i]))
                {
                    break;
                }

                bool hasNext = i + 1 < identifier.Length;
                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    // if the next character is a space, which is not considered uppercase
                    // (otherwise we wouldn't be here...)
                    // we want to ensure that the following:
                    // 'FOO bar' is rewritten as 'foo bar', and not as 'foO bar'
                    // The code was written in such a way that the first word in uppercase
                    // ends when if finds an uppercase letter followed by a lowercase letter.
                    // now a ' ' (space, (char)32) is considered not upper
                    // but in that case we still want our current character to become lowercase
                    if (char.IsSeparator(chars[i + 1]))
                    {
                        chars[i] = char.ToLowerInvariant(chars[i]);
                    }

                    break;
                }

                chars[i] = char.ToLowerInvariant(chars[i]);
            }

            return new string(chars, 0, identifier.Length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}
