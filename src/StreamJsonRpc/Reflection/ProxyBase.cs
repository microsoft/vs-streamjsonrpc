// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Abstract base class for source generated proxies.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ProxyBase : IJsonRpcClientProxyInternal
{
    private readonly JsonRpc client;
    private readonly JsonRpcProxyOptions? options;
    private readonly long? marshaledObjectHandle;
    private readonly Action? onDispose;
    private readonly ReadOnlyMemory<Type>? requestedInterfaces;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyBase"/> class.
    /// </summary>
    /// <param name="client">The <see cref="JsonRpc"/> instance that this proxy interacts with.</param>
    /// <param name="inputs">Proxy inputs.</param>
    public ProxyBase(JsonRpc client, in ProxyInputs inputs)
    {
        if (inputs.Options?.ProxySource is JsonRpcProxyOptions.ProxyImplementation.AlwaysDynamic)
        {
            throw new NotSupportedException(Resources.InterceptedProxiesRequestCannotProvideDynamicProxies);
        }

        this.client = client;
        this.options = inputs.Options;
        this.marshaledObjectHandle = inputs.MarshaledObjectHandle;
        this.onDispose = inputs.Options?.OnDispose;

        Type[] requestedInterfaces = new Type[1 + inputs.AdditionalContractInterfaces.Length + inputs.ImplementedOptionalInterfaces.Length];
        int i = 0;
        requestedInterfaces[i++] = inputs.ContractInterface;
        for (int j = 0; j < inputs.AdditionalContractInterfaces.Length; j++)
        {
            requestedInterfaces[i++] = inputs.AdditionalContractInterfaces.Span[j];
        }

        for (int j = 0; j < inputs.ImplementedOptionalInterfaces.Length; j++)
        {
            requestedInterfaces[i++] = inputs.ImplementedOptionalInterfaces.Span[j].Type;
        }

        this.requestedInterfaces = requestedInterfaces;
    }

    /// <inheritdoc/>
    public event EventHandler<string>? CallingMethod;

    /// <inheritdoc/>
    public event EventHandler<string>? CalledMethod;

    /// <inheritdoc/>
    public JsonRpc JsonRpc => this.client;

    /// <inheritdoc/>
    public bool IsDisposed => this.disposed || this.client.IsDisposed;

    /// <inheritdoc/>
    long? IJsonRpcClientProxyInternal.MarshaledObjectHandle => this.marshaledObjectHandle;

    /// <summary>
    /// Gets options related to this proxy.
    /// </summary>
    protected JsonRpcProxyOptions Options => this.options ?? JsonRpcProxyOptions.Default;

    /// <summary>
    /// Creates a source generated proxy for the specified <see cref="JsonRpc"/> and <see cref="ProxyInputs"/>.
    /// </summary>
    /// <param name="jsonRpc">The <see cref="JsonRpc"/> instance that the proxy will interact with.</param>
    /// <param name="proxyInputs">The inputs describing the contract interface, additional interfaces, and options for proxy generation.</param>
    /// <param name="startOrFail">
    /// If <see langword="true"/>, <see cref="JsonRpc.StartListening"/> is called on the <paramref name="jsonRpc"/> instance if a proxy is created,
    /// or <see cref="JsonRpc.Dispose()"/> is called if no proxy is found.
    /// </param>
    /// <returns>The created <see cref="IJsonRpcClientProxy"/> instance.</returns>
    /// <remarks>
    /// If a compatible proxy is found, it is returned; otherwise, the <see cref="JsonRpc"/> instance is disposed (if <paramref name="startOrFail"/> is <see langword="true"/>)
    /// and a <see cref="NotSupportedException"/> is thrown.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Thrown if no compatible source generated proxy can be found for the specified requirements in <paramref name="proxyInputs"/>.
    /// </exception>
    public static IJsonRpcClientProxy CreateProxy(JsonRpc jsonRpc, in ProxyInputs proxyInputs, bool startOrFail)
    {
        Requires.NotNull(jsonRpc);
        if (TryCreateProxy(jsonRpc, proxyInputs, out IJsonRpcClientProxy? proxy))
        {
            if (startOrFail)
            {
                jsonRpc.StartListening();
            }

            return proxy;
        }
        else
        {
            if (startOrFail)
            {
                jsonRpc.Dispose();
            }

            throw new NotImplementedException($"Unable to find a source generated proxy filling the specified requirements: {proxyInputs.Requirements}. Research the NativeAOT topic in the documentation at https://microsoft.github.io/vs-streamjsonrpc");
        }
    }

    /// <summary>
    /// Attempts to create a source generated proxy that implements the specified contract and additional interfaces.
    /// </summary>
    /// <param name="jsonRpc">The <see cref="JsonRpc"/> instance that the proxy will interact with.</param>
    /// <param name="proxyInputs">The inputs describing the contract interface, additional interfaces, and options for proxy generation.</param>
    /// <param name="proxy">
    /// When this method returns, contains the created <see cref="IJsonRpcClientProxy"/> if a compatible proxy was found; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a compatible proxy was found and created; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This method searches for a source generated proxy type that implements a superset of the contract and additional interfaces specified in <paramref name="proxyInputs"/>.
    /// If a matching proxy type is found, it is instantiated and returned via <paramref name="proxy"/>.
    /// If no compatible proxy is found, <paramref name="proxy"/> is set to <see langword="null"/> and the method returns <see langword="false"/>.
    /// </remarks>
    public static bool TryCreateProxy(JsonRpc jsonRpc, in ProxyInputs proxyInputs, [NotNullWhen(true)] out IJsonRpcClientProxy? proxy)
    {
        if (proxyInputs.ImplementedOptionalInterfaces.Span is not [])
        {
            proxy = null;
            return false;
        }

        // Look for a source generated proxy type first.
        // We want a proxy that implements exactly the right set of contract interfaces.
        foreach (JsonRpcProxyMappingAttribute attribute in proxyInputs.ContractInterface.GetCustomAttributes<JsonRpcProxyMappingAttribute>())
        {
            // Of the various proxies that implement the interfaces the user requires,
            // look for a match.
            if (ProxyImplementsCompatibleSetOfInterfaces(attribute.ProxyClass, proxyInputs.ContractInterface, proxyInputs.AdditionalContractInterfaces.Span, proxyInputs.Options))
            {
                // If the source generated proxy type exists, use it.
                proxy = (IJsonRpcClientProxyInternal)Activator.CreateInstance(attribute.ProxyClass, jsonRpc, proxyInputs)!;
                return proxy is not null;
            }
        }

        proxy = null;
        return false;
    }

    /// <inheritdoc/>
    public T? As<T>()
        where T : class
    {
        // If the type check fails, then the contract is definitely not implemented.
        if (this is not T thisAsThat)
        {
            return null;
        }

        if (!this.requestedInterfaces.HasValue || this.options?.AcceptProxyWithExtraInterfaces is not true)
        {
            // There's no chance this proxy implements too many interfaces.
            return thisAsThat;
        }

        foreach (Type iface in this.requestedInterfaces.Value.Span)
        {
            if (iface == typeof(T))
            {
                return thisAsThat;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.onDispose is not null)
        {
            this.onDispose();
        }
        else
        {
            this.client.Dispose();
        }
    }

    /// <summary>
    /// Invokes the <see cref="CallingMethod"/> event.
    /// </summary>
    /// <param name="method">The name of the method to be invoked.</param>
    protected void OnCallingMethod(string method) => this.CallingMethod?.Invoke(this, method);

    /// <summary>
    /// Invokes the <see cref="CalledMethod"/> event.
    /// </summary>
    /// <param name="method">The name of the method that was invoked.</param>
    protected void OnCalledMethod(string method) => this.CalledMethod?.Invoke(this, method);

    /// <summary>
    /// Determines whether a proxy class implements a compatible set of interfaces.
    /// </summary>
    /// <param name="proxyClass">The type of the proxy class to be evaluated. This type must implement the specified interfaces.</param>
    /// <param name="contractInterface">The primary contract interface that the proxy class must implement.</param>
    /// <param name="additionalContractInterfaces">A span of additional contract interfaces that the proxy class must also implement.</param>
    /// <param name="options">Options that influence the compatibility check, such as whether extra interfaces are acceptable.</param>
    /// <returns><see langword="true"/> if the proxy class implements the specified contract interface and additional interfaces,
    /// and optionally extra interfaces if allowed by the options; otherwise, <see langword="false"/>.</returns>
    /// <remarks>This method checks if the proxy class implements exactly the interfaces specified by the
    /// contract and additional interfaces, unless the <paramref name="options"/> allow for extra interfaces.
    /// It also removes any boilerplate interfaces from consideration.
    /// </remarks>
    private static bool ProxyImplementsCompatibleSetOfInterfaces(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type proxyClass,
        Type contractInterface,
        ReadOnlySpan<Type> additionalContractInterfaces,
        JsonRpcProxyOptions? options)
    {
        HashSet<Type> proxyInterfaces = [.. proxyClass.GetInterfaces()];
        if (!proxyInterfaces.Remove(contractInterface))
        {
            return false;
        }

        foreach (Type addl in additionalContractInterfaces)
        {
            if (!proxyInterfaces.Remove(addl))
            {
                return false;
            }
        }

        // At this point, we've ensured that the proxy implements the contract interface and any additional interfaces.
        // But does it implement *more* than the caller wants?
        if (options?.AcceptProxyWithExtraInterfaces is true)
        {
            // If the caller accepts proxies with extra interfaces, then we don't care what else the proxy implements.
            return true;
        }

        // Remove the boilerplate interfaces from the set.
        proxyInterfaces.ExceptWith(typeof(ProxyBase).GetInterfaces());

        // Are there any remaining interfaces? If so, they're alright only if they are base types of the interfaces we were looking for.
        foreach (Type remaining in proxyInterfaces)
        {
            if (remaining.IsAssignableFrom(contractInterface))
            {
                continue;
            }

            foreach (Type addl in additionalContractInterfaces)
            {
                if (remaining.IsAssignableFrom(addl))
                {
                    continue;
                }
            }

            // This is an extra, unwanted interface.
            return false;
        }

        return true;
    }
}
