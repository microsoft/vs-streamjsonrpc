using Microsoft.CodeAnalysis.CSharp;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterceptionModel(ProxyModel? Proxy, AttachSignature Signature, ImmutableEquatableArray<InterceptableLocation> Locations)
{
    internal void WriteInterceptor(SourceWriter writer)
    {
        writer.WriteLine();

        foreach (InterceptableLocation location in this.Locations)
        {
            writer.WriteLine(location.GetInterceptsLocationAttributeSyntax());
        }

        string interceptingMethodName = this.Proxy is not null ? $"Attach{this.Proxy.Name.Replace('.', '_')}" : "Attach_Unknown";

        string CreateProxyExpression(string inputs, string? jsonRpcCreation = null) =>
            this.Proxy is null ? $"global::StreamJsonRpc.Reflection.ProxyBase.CreateProxy({jsonRpcCreation ?? "jsonRpc"}, new() {inputs}, {(jsonRpcCreation is not null ? "true" : "false")})" :
            jsonRpcCreation is null ? $"new {this.Proxy.Name}(jsonRpc, new() {inputs})" :
            $"StartListening(new {this.Proxy.Name}({jsonRpcCreation}, new() {inputs}))";

        switch (this.Signature)
        {
            case AttachSignature.InstanceGeneric:
                writer.WriteLine($$"""
                    internal static T {{interceptingMethodName}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc)
                        where T : class
                        => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }")}};
                    """);
                break;
            case AttachSignature.InstanceGenericOptions:
                writer.WriteLine($$"""
                    internal static T {{interceptingMethodName}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        where T : class
                        => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T), Options = options }")}};
                    """);
                break;
            case AttachSignature.InstanceNonGeneric:
                writer.WriteLine($$"""
                    internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType)
                        => {{CreateProxyExpression("{ ContractInterface = interfaceType }")}};
                    """);
                break;
            case AttachSignature.InstanceNonGenericOptions:
                writer.WriteLine($$"""
                    internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        => {{CreateProxyExpression("{ ContractInterface = interfaceType, Options = options }")}};
                    """);
                break;
            case AttachSignature.InstanceNonGenericSpanOptions:
                writer.WriteLine($$"""
                    internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.ReadOnlySpan<global::System.Type> interfaceTypes, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        => {{CreateProxyExpression("{ ContractInterface = interfaceTypes[0], AdditionalContractInterfaces = interfaceTypes.Slice(1).ToArray(), Options = options }")}};
                    """);
                break;
            case AttachSignature.StaticGenericStream:
                writer.WriteLine($$"""
                    internal static T {{interceptingMethodName}}<T>(global::System.IO.Stream stream)
                        => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "new(stream)")}};
                    """);
                break;
            case AttachSignature.StaticGenericStreamStream:
                writer.WriteLine($$"""
                    internal static T {{interceptingMethodName}}<T>(global::System.IO.Stream sendingStream, global::System.IO.Stream receivingStream)
                        => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "new(sendingStream, receivingStream)")}};
                    """);
                break;
            case AttachSignature.StaticGenericHandler:
                writer.WriteLine($$"""
                    internal static T {{interceptingMethodName}}<T>(global::StreamJsonRpc.IJsonRpcMessageHandler handler)
                        => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "new(handler)")}};
                    """);
                break;
            case AttachSignature.StaticGenericHandlerOptions:
                writer.WriteLine($$"""
                    internal static T {{interceptingMethodName}}<T>(global::StreamJsonRpc.IJsonRpcMessageHandler handler, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T), Options = options }", "new(handler)")}};
                    """);
                break;
            default:
                throw new NotSupportedException();
        }
    }
}
