using System.Diagnostics.CodeAnalysis;

namespace StreamJsonRpc.Reflection;

internal interface IProxyFactory
{
    IJsonRpcClientProxyInternal CreateProxy(JsonRpc jsonRpc, ProxyInputs proxyInputs);
}

[RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
internal class DefaultProxyFactory : IProxyFactory
{
    internal static readonly DefaultProxyFactory Instance = new();

    private DefaultProxyFactory()
    {
    }

    public IJsonRpcClientProxyInternal CreateProxy(JsonRpc jsonRpc, ProxyInputs proxyInputs) => jsonRpc.CreateProxy(proxyInputs);
}

internal class NoDynamicProxyFactory : IProxyFactory
{
    internal static readonly NoDynamicProxyFactory Instance = new();

    private NoDynamicProxyFactory()
    {
    }

    public IJsonRpcClientProxyInternal CreateProxy(JsonRpc jsonRpc, ProxyInputs proxyInputs)
        => ProxyBase.TryCreateProxy(jsonRpc, proxyInputs, out IJsonRpcClientProxy? proxy)
            ? (IJsonRpcClientProxyInternal)proxy
            : throw new NotImplementedException("No source generated proxy is available for the requested interface(s), and dynamic proxies are forbidden by the options.");
}
