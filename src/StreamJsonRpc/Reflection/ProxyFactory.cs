using System.Diagnostics.CodeAnalysis;

namespace StreamJsonRpc.Reflection;

internal abstract class ProxyFactory
{
    internal static ProxyFactory Default
    {
        [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
        get => DefaultProxyFactory.Instance;
    }

    internal static ProxyFactory NoDynamic => NoDynamicProxyFactory.Instance;

    internal abstract IJsonRpcClientProxyInternal CreateProxy(JsonRpc jsonRpc, ProxyInputs proxyInputs);

    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    private class DefaultProxyFactory : ProxyFactory
    {
        internal static readonly DefaultProxyFactory Instance = new();

        private DefaultProxyFactory()
        {
        }

        internal override IJsonRpcClientProxyInternal CreateProxy(JsonRpc jsonRpc, ProxyInputs proxyInputs) => jsonRpc.CreateProxy(proxyInputs);
    }

    private class NoDynamicProxyFactory : ProxyFactory
    {
        internal static readonly NoDynamicProxyFactory Instance = new();

        private NoDynamicProxyFactory()
        {
        }

        internal override IJsonRpcClientProxyInternal CreateProxy(JsonRpc jsonRpc, ProxyInputs proxyInputs)
            => ProxyBase.TryCreateProxy(jsonRpc, proxyInputs, out IJsonRpcClientProxy? proxy)
                ? (IJsonRpcClientProxyInternal)proxy
                : throw proxyInputs.CreateNoSourceGeneratedProxyException();
    }
}
