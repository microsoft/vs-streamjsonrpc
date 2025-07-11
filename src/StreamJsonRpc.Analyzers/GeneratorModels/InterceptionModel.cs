using Microsoft.CodeAnalysis.CSharp;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterceptionModel(ProxyModel Proxy, AttachSignature Signature, ImmutableEquatableArray<InterceptableLocation> Locations)
{
    internal void WriteInterceptor(SourceWriter writer)
    {
        writer.WriteLine();

        foreach (InterceptableLocation location in this.Locations)
        {
            writer.WriteLine(location.GetInterceptsLocationAttributeSyntax());
        }

        switch (this.Signature)
        {
            case AttachSignature.InstanceGeneric:
                writer.WriteLine($$"""
                    internal static T Attach{{this.Proxy.Name}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc)
                        where T : class
                    {
                        return (T)(object)new {{this.Proxy.Name}}(jsonRpc, null, null, null);
                    }
                    """);
                break;
            case AttachSignature.InstanceGenericOptions:
                writer.WriteLine($$"""
                    internal static T Attach{{this.Proxy.Name}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        where T : class
                    {
                        return (T)(object)new {{this.Proxy.Name}}(jsonRpc, options, null, null);
                    }
                    """);
                break;
            case AttachSignature.InstanceNonGeneric:
                writer.WriteLine($$"""
                    internal static object Attach{{this.Proxy.Name}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType)
                        => new {{this.Proxy.Name}}(jsonRpc, null, null, null);
                    """);
                break;
            case AttachSignature.InstanceNonGenericOptions:
                writer.WriteLine($$"""
                    internal static object Attach{{this.Proxy.Name}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.Type interfaceType, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        => new {{this.Proxy.Name}}(jsonRpc, options, null, null);
                    """);
                break;
            case AttachSignature.InstanceNonGenericSpanOptions:
                writer.WriteLine($$"""
                    internal static object Attach{{this.Proxy.Name}}(this global::StreamJsonRpc.JsonRpc jsonRpc, global::System.ReadOnlySpan<global::System.Type> interfaceTypes, global::StreamJsonRpc.JsonRpcProxyOptions? options)
                        => new {{this.Proxy.Name}}(jsonRpc, options, null, null);
                    """);
                break;
            default:
                throw new NotSupportedException();
        }
    }
}
