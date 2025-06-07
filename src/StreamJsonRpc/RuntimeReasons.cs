// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

internal static class RuntimeReasons
{
    internal const string CloseGenerics = "This code closes generic types or methods at runtime.";

    internal const string ExtractTypes = "This code uses Reflection to extract types from other Types.";

    internal const string RefEmit = "This code generates IL at runtime and executes it.";

    internal const string Formatters = "This code uses a formatter/serializer that hasn't been hardened to avoid dynamic code.";

    internal const string LoadType = "This code loads a type from a string at runtime.";

    internal const string UntypedRpcTarget = "This code adds an untyped object as an RPC target.";
}
