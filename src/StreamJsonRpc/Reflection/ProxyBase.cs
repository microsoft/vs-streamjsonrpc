// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PolyType;
using PolyType.Abstractions;
using PolyType.Utilities;
using StreamJsonRpc.Reflection;

// Instruct PolyType to generate shapes with methods included for .NET interfaces that we make special allowances to treat as if they were declared with [RpcMarshalable].
// Generic interfaces require very special handling to work in NativeAOT environments.
[assembly: TypeShapeExtension(typeof(IDisposable), IncludeMethods = MethodShapeFlags.PublicInstance)]
[assembly: TypeShapeExtension(typeof(IObserver<>), IncludeMethods = MethodShapeFlags.PublicInstance, AssociatedTypes = [typeof(ProxyBase.ObserverProxyActivator<>)], Requirements = TypeShapeRequirements.Constructor)]

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Abstract base class for source generated proxies.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ProxyBase : IJsonRpcClientProxyInternal
{
    /// <summary>
    /// A map of .NET BCL types that we have special handling for so that users can use them in their RPC interfaces
    /// as if they had <see cref="RpcMarshalableAttribute"/> applied to them,
    /// to their activation helpers.
    /// </summary>
    private static readonly FrozenDictionary<Type, Type> BclTypesTreatedAsMarshalable = new Dictionary<Type, Type>
    {
        [typeof(IObserver<>)] = typeof(ObserverProxyActivator<>),
    }.ToFrozenDictionary();

    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<Type, int>> OptionalInterfaceCodeCache = [];

    private readonly JsonRpc client;
    private readonly ProxyInputs inputs;
    private readonly ReadOnlyMemory<Type>? requestedInterfaces;
    private readonly IReadOnlyDictionary<Type, int> optionalInterfaceCodes;
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
        this.inputs = inputs;

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

        this.optionalInterfaceCodes = OptionalInterfaceCodeCache.GetOrAdd(
            inputs.ContractInterface,
            static contract =>
            {
                RpcMarshalableAttribute? mainAttribute = (RpcMarshalableAttribute?)contract.GetCustomAttribute(typeof(RpcMarshalableAttribute), inherit: false);
                if (mainAttribute is null)
                {
                    return ImmutableDictionary<Type, int>.Empty;
                }

                RpcMarshalableOptionalInterfaceAttribute[] optionalInterfaceAttributes = MessageFormatterRpcMarshaledContextTracker.GetMarshalableOptionalInterfaces(contract, mainAttribute);
                if (optionalInterfaceAttributes is [])
                {
                    return ImmutableDictionary<Type, int>.Empty;
                }

                (Type, int?)[] sortedOptionalInterfaces = optionalInterfaceAttributes.Select(optionalInterfaceAttributes => (optionalInterfaceAttributes.OptionalInterface, (int?)optionalInterfaceAttributes.OptionalInterfaceCode)).ToArray();
                ProxyInputs.SortRpcInterfaces(sortedOptionalInterfaces);

                // COMPAT warning: dynamic proxies only ever consider the selected subset of optional interfaces when assigning prefixes.
                // But this (newer) code considers all possible optional interfaces whether they are selected or not, so that we can share the results
                // across all such proxies.
                // It also makes sense that these should be stable assignments so the client knows how to call a particular method, even though it may not
                // know all the interfaces the server implements.
                // So if we ever get into a compat issue here, we should probably fix the algorithm in ProxyGeneration.cs to match the one we have here.
                Dictionary<Type, int> codes = [];
                foreach ((Type optionalIface, _) in sortedOptionalInterfaces)
                {
                    // Access the interface through the attribute to avoid trim safety warnings.
                    RpcMarshalableOptionalInterfaceAttribute attribute = optionalInterfaceAttributes.First(att => att.OptionalInterface == optionalIface);
                    AddInterfaceCode(optionalIface, attribute.OptionalInterfaceCode);

                    Type[] baseInterfaces = attribute.OptionalInterface.GetInterfaces();
                    foreach (Type iface in baseInterfaces)
                    {
                        AddInterfaceCode(iface, attribute.OptionalInterfaceCode);
                    }
                }

                void AddInterfaceCode(Type iface, int code)
                {
                    // We never assign a code to an interface that the contract derives from.
                    // We also follow a first one wins policy (in the sorted collection our caller is enumerating).
                    if (!iface.IsAssignableFrom(contract) && !codes.ContainsKey(iface))
                    {
                        codes.Add(iface, code);
                    }
                }

                return codes;
            });
    }

    /// <inheritdoc/>
    public event EventHandler<string>? CallingMethod;

    /// <inheritdoc/>
    public event EventHandler<string>? CalledMethod;

    /// <summary>
    /// A stub interface used to trigger generation of a source generated proxy for <see cref="IObserver{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of observed value.</typeparam>
    /// <remarks>
    /// The proxy is activated by <see cref="ObserverProxyActivator{T}"/>.
    /// </remarks>
    [RpcMarshalable]
    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    internal interface IObserverProxyGenerator<T> : IObserver<T>, IDisposable;

    /// <summary>
    /// A non-generic interface that can activate a generic proxy type.
    /// </summary>
    private interface IProxyActivator
    {
        IJsonRpcClientProxy Activate(JsonRpc client, in ProxyInputs inputs);
    }

    /// <inheritdoc/>
    public JsonRpc JsonRpc => this.client;

    /// <inheritdoc/>
    public bool IsDisposed => this.disposed || this.client.IsDisposed;

    /// <inheritdoc/>
    long? IJsonRpcClientProxyInternal.MarshaledObjectHandle => this.inputs.MarshaledObjectHandle;

    /// <summary>
    /// Gets options related to this proxy.
    /// </summary>
    protected JsonRpcProxyOptions Options => this.inputs.Options ?? JsonRpcProxyOptions.Default;

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
        // Special case for certain interfaces which we document that we
        // can create proxies for without any effort on the user's part.
        if (proxyInputs.AdditionalContractInterfaces.IsEmpty && proxyInputs.ImplementedOptionalInterfaces.IsEmpty)
        {
            if (proxyInputs.ContractInterface == typeof(IDisposable))
            {
                proxy = new ProxyForIDisposable(jsonRpc, proxyInputs);
                return true;
            }
            else if (proxyInputs.ContractInterface is { GenericTypeArguments.Length: 1 } && proxyInputs.ContractInterfaceShape is not null)
            {
                // To avoid having to dynamically close a generic type, we utilize PolyType associated type shapes to get our activation class,
                // which is generic and therefore the NativeAOT compiler will have precompiled it and the proxy it depends on.
                if (BclTypesTreatedAsMarshalable.TryGetValue(proxyInputs.ContractInterface.GetGenericTypeDefinition(), out Type? associatedActivatorType))
                {
                    IObjectTypeShape? proxyGenerationShape = (IObjectTypeShape?)proxyInputs.ContractInterfaceShape.GetAssociatedTypeShape(associatedActivatorType);
                    if (proxyGenerationShape?.GetDefaultConstructor() is { } ctor)
                    {
                        IProxyActivator activator = (IProxyActivator)ctor();
                        proxy = activator.Activate(jsonRpc, proxyInputs);
                        return true;
                    }
                }
            }
        }

        // Look for a source generated proxy type first.
        // We want a proxy that implements exactly the right set of contract interfaces.
        foreach (JsonRpcProxyMappingAttribute attribute in proxyInputs.ContractInterface.GetCustomAttributes<JsonRpcProxyMappingAttribute>())
        {
            // Of the various proxies that implement the interfaces the user requires,
            // look for a match.
            if (ProxyImplementsCompatibleSetOfInterfaces(
                attribute.ProxyClass,
                proxyInputs.ContractInterface,
                proxyInputs.AdditionalContractInterfaces.Span,
                proxyInputs.ImplementedOptionalInterfaces.Span,
                proxyInputs.Options))
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
    public bool Is(Type type)
    {
        Requires.NotNull(type);

        bool assignable = type.IsAssignableFrom(this.GetType());

        // If the type check fails, then the contract is definitely not implemented.
        if (!assignable)
        {
            return false;
        }

        if (!this.requestedInterfaces.HasValue || !this.Options.AcceptProxyWithExtraInterfaces)
        {
            // There's no chance this proxy implements too many interfaces,
            // so fallback to assignability check.
            return assignable;
        }

        foreach (Type iface in this.requestedInterfaces.Value.Span)
        {
            if (type.IsAssignableFrom(iface))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.inputs.Options?.OnDispose is Action dispose)
        {
            dispose();
        }
        else
        {
            this.client.Dispose();
        }
    }

    /// <summary>
    /// Applies the prescribed transform to obtain the RPC method name from an existing method.
    /// </summary>
    /// <param name="name">The name of the method in CLR terms.</param>
    /// <param name="declaringType">The declaring type of the method.</param>
    /// <returns>The RPC name of the method.</returns>
    protected string TransformMethodName(string name, Type declaringType)
        => this.Options.MethodNameTransform(this.TryGetOptionalInterfaceCode(Requires.NotNull(declaringType), out int code) ? $"{code}.{name}" : name);

    /// <summary>
    /// Transforms the specified event name using the configured event name transformation logic.
    /// </summary>
    /// <param name="name">The name of the event to transform in CLR terms.</param>
    /// <param name="declaringType">The type that declares the event.</param>
    /// <returns>The name of the RPC method invoked when the event is raised.</returns>
    protected string TransformEventName(string name, Type declaringType)
        => this.Options.EventNameTransform(this.TryGetOptionalInterfaceCode(Requires.NotNull(declaringType), out int code) ? $"{code}.{name}" : name);

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
    /// <param name="implementedOptionalInterfaces">Another span of contract interfaces that the proxy class must implement.</param>
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
        ReadOnlySpan<(Type Type, int Code)> implementedOptionalInterfaces,
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

        foreach ((Type addl, _) in implementedOptionalInterfaces)
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

            foreach ((Type addl, _) in implementedOptionalInterfaces)
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

    private bool TryGetOptionalInterfaceCode(Type iface, [MaybeNullWhen(false)] out int code)
    {
        // Never produce a code when the interface being tested for is assignable from the primary contract interface
        // or any of the additional contract interfaces.
        if (iface.IsAssignableFrom(this.inputs.ContractInterface))
        {
            code = default;
            return false;
        }

        foreach (Type addl in this.inputs.AdditionalContractInterfaces.Span)
        {
            if (iface.IsAssignableFrom(addl))
            {
                code = default;
                return false;
            }
        }

        return this.optionalInterfaceCodes.TryGetValue(iface, out code);
    }

    /// <summary>
    /// A helper class that can activate a closed generic proxy for <see cref="IObserver{T}"/> in a NativeAOT-compatible way.
    /// </summary>
    /// <typeparam name="T">The type argument for the <see cref="IObserver{T}"/> proxy.</typeparam>
    /// <remarks>
    /// <para>
    /// Because this class has a hard-coded <typeparamref name="T"/> type parameter, it can activate the proxy in a NativeAOT-safe way.
    /// Instances of this class are obtained via PolyType associated type shapes in order to avoid any code ever having to call
    /// <see cref="Type.MakeGenericType(Type[])"/> or <see cref="MethodInfo.MakeGenericMethod(Type[])"/> which requires a runtime
    /// that supports dynamic code generation.
    /// </para>
    /// <para>
    /// The proxy this class activates is generated by the source generator in response to <see cref="IObserverProxyGenerator{T}"/>.
    /// </para>
    /// <para>
    /// This class may be hidden from users, but it is here to trigger source generators to emit code that references it,
    /// so treat this class just like any other legitimate public API by honoring binary API compatibility requirements.
    /// </para>
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class ObserverProxyActivator<T> : IProxyActivator
    {
        IJsonRpcClientProxy IProxyActivator.Activate(JsonRpc client, in ProxyInputs inputs) => new Generated.StreamJsonRpc_Reflection_ProxyBase_IObserverProxyGenerator_Proxy<T>(client, inputs);
    }

    /// <summary>
    /// A minimal <see cref="ProxyBase"/> derived class that serves as an <see cref="IDisposable"/> proxy.
    /// </summary>
    /// <param name="client"><inheritdoc cref="ProxyBase(JsonRpc, in ProxyInputs)" path="/param[@name='client']"/></param>
    /// <param name="inputs"><inheritdoc cref="ProxyBase(JsonRpc, in ProxyInputs)" path="/param[@name='inputs']"/></param>
    /// <remarks>
    /// The base class already implements the <see cref="IDisposable"/> interface.
    /// The only reason we have to declare this class is because <see cref="ProxyBase"/> is <see langword="abstract"/>
    /// and we need a concrete type to instantiate.
    /// </remarks>
    private class ProxyForIDisposable(JsonRpc client, in ProxyInputs inputs) : ProxyBase(client, inputs), IDisposable;
}
