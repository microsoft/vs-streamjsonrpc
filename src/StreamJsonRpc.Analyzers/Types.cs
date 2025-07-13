// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Analyzers;

internal static class Types
{
    internal static class RpcContractAttribute
    {
        internal const string FullName = "StreamJsonRpc.RpcContractAttribute";
    }

    internal static class ExportRpcContractProxiesAttribute
    {
        internal const string Name = "ExportRpcContractProxiesAttribute";

        internal const string FullName = $"StreamJsonRpc.{Name}";

        internal const string ForbidExternalProxyGeneration = "ForbidExternalProxyGeneration";
    }

    internal static class RpcProxyMappingAttribute
    {
        internal const string Name = "RpcProxyMappingAttribute";

        internal const string FullName = $"StreamJsonRpc.Reflection.{Name}";
    }

    internal static class JsonRpcMethodAttribute
    {
        internal const string FullName = "StreamJsonRpc.JsonRpcMethodAttribute";
    }
}
