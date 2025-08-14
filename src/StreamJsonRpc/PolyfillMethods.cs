// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

internal static class PolyfillMethods
{
#if NETSTANDARD2_0
    internal static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        => (key, value) = (pair.Key, pair.Value);
#endif
}
