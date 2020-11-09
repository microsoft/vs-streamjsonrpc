// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Runtime.InteropServices;

    internal static class Hex
    {
        private static readonly byte[] HexBytes = new byte[] { (byte)'0', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9', (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f' };
        private static readonly byte[] ReverseHexDigits = BuildReverseHexDigits();

        internal static void Encode(ReadOnlySpan<byte> src, ref Span<char> dest)
        {
            Span<byte> bytes = MemoryMarshal.Cast<char, byte>(dest);

            // Inspired by http://stackoverflow.com/questions/623104/c-byte-to-hex-string/3974535#3974535
            int lengthInNibbles = src.Length * 2;

            for (int i = 0; i < (lengthInNibbles & -2); i++)
            {
                int index0 = +i >> 1;
                var b = (byte)(src[index0] >> 4);
                bytes[(2 * i) + 1] = 0;
                bytes[2 * i++] = HexBytes[b];

                b = (byte)(src[index0] & 0x0F);
                bytes[(2 * i) + 1] = 0;
                bytes[2 * i] = HexBytes[b];
            }

            dest = dest.Slice(lengthInNibbles);
        }

        internal static void Decode(ReadOnlySpan<char> value, Span<byte> bytes)
        {
            for (int i = 0; i < value.Length; i++)
            {
                int c1 = ReverseHexDigits[value[i++] - '0'] << 4;
                int c2 = ReverseHexDigits[value[i] - '0'];

                bytes[i >> 1] = (byte)(c1 + c2);
            }
        }

        private static byte[] BuildReverseHexDigits()
        {
            var bytes = new byte['f' - '0' + 1];

            for (int i = 0; i < 10; i++)
            {
                bytes[i] = (byte)i;
            }

            for (int i = 10; i < 16; i++)
            {
                bytes[i + 'a' - '0' - 0x0a] = (byte)i;
                bytes[i + 'A' - '0' - 0x0a] = (byte)i;
            }

            return bytes;
        }
    }
}
