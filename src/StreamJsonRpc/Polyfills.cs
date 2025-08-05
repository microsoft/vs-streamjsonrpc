// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;

namespace StreamJsonRpc;

internal static class Polyfills
{
#if !(NETSTANDARD2_1_OR_GREATER || NET)
    internal static unsafe string GetString(this Encoding encoding, ReadOnlySpan<byte> utf8Bytes)
    {
        fixed (byte* pBytes = utf8Bytes)
        {
            return encoding.GetString(pBytes, utf8Bytes.Length);
        }
    }
#endif
}
