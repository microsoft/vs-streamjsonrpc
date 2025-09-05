// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Analyzers;

internal static class Types
{
    internal static class RpcMarshalableAttribute
    {
        internal const string FullName = "StreamJsonRpc.RpcMarshalableAttribute";

        internal const string CallScopedLifetime = "CallScopedLifetime";

        internal const string IsOptional = "IsOptional";
    }

    internal static class RpcMarshalableOptionalInterfaceAttribute
    {
        internal const string FullName = "StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute";
    }

    internal static class JsonRpcContractAttribute
    {
        internal const string FullName = "StreamJsonRpc.JsonRpcContractAttribute";
    }

    internal static class JsonRpcProxyAttribute
    {
        internal const string FullName = "StreamJsonRpc.JsonRpcProxyAttribute`1";
    }

    internal static class JsonRpcProxyInterfaceGroupAttribute
    {
        internal const string FullName = "StreamJsonRpc.JsonRpcProxyInterfaceGroupAttribute";
    }

    internal static class ExportRpcContractProxiesAttribute
    {
        internal const string Name = "ExportRpcContractProxiesAttribute";

        internal const string FullName = $"StreamJsonRpc.{Name}";

        internal const string ForbidExternalProxyGeneration = "ForbidExternalProxyGeneration";
    }

    internal static class JsonRpcProxyMappingAttribute
    {
        internal const string Name = "JsonRpcProxyMappingAttribute";

        internal const string FullName = $"StreamJsonRpc.Reflection.{Name}";
    }

    internal static class JsonRpcMethodAttribute
    {
        internal const string FullName = "StreamJsonRpc.JsonRpcMethodAttribute";
    }

    internal static class MethodShapeAttribute
    {
        internal const string FullName = "PolyType.MethodShapeAttribute";

        internal const string NameProperty = "Name";
    }
}
