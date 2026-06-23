using Microsoft.CodeAnalysis.CSharp;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterceptionModel(ProxyModel? Proxy, AttachSignature Signature, ImmutableEquatableArray<InterceptableLocation> Locations, bool UseReflectionActivation)
{
    internal void WriteInterceptor(SourceWriter writer)
    {
        writer.WriteLine();

        foreach (InterceptableLocation location in this.Locations)
        {
            writer.WriteLine(location.GetInterceptsLocationAttributeSyntax());
        }

        string interceptingMethodName = this.Proxy is not null ? $"Attach{this.Proxy.Name.Replace('.', '_')}" : "Attach_Unknown";

        string CreateProxyInputs(string inputs) => $"new() {inputs}";

        string CreateProxyExpression(string inputs, string? jsonRpcCreation = null)
        {
            if (!this.UseReflectionActivation)
            {
                return this.Proxy is null ? $"global::StreamJsonRpc.Reflection.ProxyBase.CreateProxy({jsonRpcCreation ?? "jsonRpc"}, new() {inputs}, {(jsonRpcCreation is not null ? "true" : "false")})" :
                jsonRpcCreation is null ? $"new {this.Proxy.Name}(jsonRpc, new() {inputs})" :
                $"StartListening(new {this.Proxy.Name}({jsonRpcCreation}, new() {inputs}))";
            }

            string proxyInputs = CreateProxyInputs(inputs);
            string proxyExpression = jsonRpcCreation is null
                ? $"CreateProxy(jsonRpc, {proxyInputs}, disposeJsonRpcOnFailure: false)"
                : $"StartListening(CreateProxy({jsonRpcCreation}, {proxyInputs}, disposeJsonRpcOnFailure: true))";

            return proxyExpression;
        }

        switch (this.Signature)
        {
            case AttachSignature.InstanceGeneric:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static T {{interceptingMethodName}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc)
                            where T : class
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }")}};
                        """
                    : $$"""
                        internal static T {{interceptingMethodName}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc)
                            where T : class
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }")}};
                        """);
                break;
            case AttachSignature.InstanceGenericOptions:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static T {{interceptingMethodName}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            where T : class
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T), Options = options }")}};
                        """
                    : $$"""
                        internal static T {{interceptingMethodName}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            where T : class
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T), Options = options }")}};
                        """);
                break;
            case AttachSignature.InstanceNonGeneric:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType)
                            => {{CreateProxyExpression("{ ContractInterface = interfaceType }")}};
                        """
                    : $$"""
                        internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType)
                            => {{CreateProxyExpression("{ ContractInterface = interfaceType }")}};
                        """);
                break;
            case AttachSignature.InstanceNonGenericOptions:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            => {{CreateProxyExpression("{ ContractInterface = interfaceType, Options = options }")}};
                        """
                    : $$"""
                        internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            => {{CreateProxyExpression("{ ContractInterface = interfaceType, Options = options }")}};
                        """);
                break;
            case AttachSignature.InstanceNonGenericSpanOptions:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.ReadOnlySpan<global::System.Type> interfaceTypes, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            => {{CreateProxyExpression("{ ContractInterface = interfaceTypes[0], AdditionalContractInterfaces = interfaceTypes.Slice(1).ToArray(), Options = options }")}};
                        """
                    : $$"""
                        internal static object {{interceptingMethodName}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.ReadOnlySpan<global::System.Type> interfaceTypes, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            => {{CreateProxyExpression("{ ContractInterface = interfaceTypes[0], AdditionalContractInterfaces = interfaceTypes.Slice(1).ToArray(), Options = options }")}};
                        """);
                break;
            case AttachSignature.StaticGenericStream:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static T {{interceptingMethodName}}<T>(global::System.IO.Stream stream)
                        {
                            global::StreamJsonRpc.JsonRpc jsonRpc = new(stream);
                            return (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "jsonRpc")}};
                        }
                        """
                    : $$"""
                        internal static T {{interceptingMethodName}}<T>(global::System.IO.Stream stream)
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "new(stream)")}};
                        """);
                break;
            case AttachSignature.StaticGenericStreamStream:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static T {{interceptingMethodName}}<T>(global::System.IO.Stream sendingStream, global::System.IO.Stream receivingStream)
                        {
                            global::StreamJsonRpc.JsonRpc jsonRpc = new(sendingStream, receivingStream);
                            return (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "jsonRpc")}};
                        }
                        """
                    : $$"""
                        internal static T {{interceptingMethodName}}<T>(global::System.IO.Stream sendingStream, global::System.IO.Stream receivingStream)
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "new(sendingStream, receivingStream)")}};
                        """);
                break;
            case AttachSignature.StaticGenericHandler:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static T {{interceptingMethodName}}<T>(global::StreamJsonRpc.IJsonRpcMessageHandler handler)
                        {
                            global::StreamJsonRpc.JsonRpc jsonRpc = new(handler);
                            return (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "jsonRpc")}};
                        }
                        """
                    : $$"""
                        internal static T {{interceptingMethodName}}<T>(global::StreamJsonRpc.IJsonRpcMessageHandler handler)
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T) }", "new(handler)")}};
                        """);
                break;
            case AttachSignature.StaticGenericHandlerOptions:
                writer.WriteLine(this.UseReflectionActivation
                    ? $$"""
                        internal static T {{interceptingMethodName}}<T>(global::StreamJsonRpc.IJsonRpcMessageHandler handler, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        {
                            global::StreamJsonRpc.JsonRpc jsonRpc = new(handler);
                            return (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T), Options = options }", "jsonRpc")}};
                        }
                        """
                    : $$"""
                        internal static T {{interceptingMethodName}}<T>(global::StreamJsonRpc.IJsonRpcMessageHandler handler, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                            => (T)(object){{CreateProxyExpression("{ ContractInterface = typeof(T), Options = options }", "new(handler)")}};
                        """);
                break;
            default:
                throw new NotSupportedException();
        }
    }
}
