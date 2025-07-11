using Microsoft.CodeAnalysis.CSharp;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterceptionModel(ProxyModel Proxy, AttachSignature Signature, ImmutableEquatableArray<InterceptableLocation> Locations)
{
    internal void WriteInterceptor(SourceWriter writer)
    {
        foreach (InterceptableLocation location in this.Locations)
        {
            writer.WriteLine(location.GetInterceptsLocationAttributeSyntax());
        }

        // TODO: for overloads that take Options, consider whether the caller
        // wants us to avoid source generated proxies and revert to old behavior in that case.

        switch (this.Signature)
        {
            case AttachSignature.InstanceGeneric:
                writer.WriteLine($$"""
                    internal static T Attach{{this.Proxy.Name}}<T>(this global::StreamJsonRpc.JsonRpc jsonRpc)
                        where T : class
                    {
                        return (T)(object)new {{this.Proxy.Name}}(jsonRpc, JsonRpcProxyOptions.Default, null, null);
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
            default: throw new NotSupportedException();
        }
    }
}
