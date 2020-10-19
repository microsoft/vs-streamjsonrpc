// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Diagnostics.CodeAnalysis;
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

            char[] chars = identifier.ToCharArray();

            for (int i = 0; i < chars.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i]))
                {
                    break;
                }

                bool hasNext = i + 1 < chars.Length;
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

            return new string(chars);
        }
    }
}
