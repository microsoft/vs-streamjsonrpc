// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using PolyType;
using static System.FormattableString;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Tracks objects that get marshaled using the general marshaling protocol.
/// </summary>
internal partial class MessageFormatterRpcMarshaledContextTracker
{
    private static readonly IReadOnlyCollection<(Type ImplicitlyMarshaledType, JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions, RpcMarshalableAttribute Attribute)> ImplicitlyMarshaledTypes = new (Type, JsonRpcProxyOptions, JsonRpcTargetOptions, RpcMarshalableAttribute)[]
    {
        (typeof(IDisposable), new JsonRpcProxyOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase }, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase }, new RpcMarshalableAttribute()),

        // IObserver<T> support requires special recognition of OnCompleted and OnError be considered terminating calls.
        (
            typeof(IObserver<>),
            new JsonRpcProxyOptions
            {
                MethodNameTransform = CommonMethodNameTransforms.CamelCase,
                OnProxyConstructed = (IJsonRpcClientProxyInternal proxy) =>
                {
                    proxy.CalledMethod += (sender, methodName) =>
                    {
                        // When OnError or OnCompleted is called, per IObserver<T> patterns that's implicitly a termination of the connection.
                        if (methodName == nameof(IObserver<int>.OnError) || methodName == nameof(IObserver<int>.OnCompleted))
                        {
                            ((IDisposable)sender!).Dispose();
                        }
                    };
                    proxy.CallingMethod += (sender, methodName) =>
                    {
                        // Any RPC method call on IObserver<T> shouldn't happen if it has already been completed.
                        Verify.NotDisposed((IDisposableObservable)sender!);
                    };
                },
            },
            new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase },
            new RpcMarshalableAttribute()),
    };

    private static readonly ConcurrentDictionary<Type, (JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions, RpcMarshalableAttribute Attribute)> MarshaledTypes = new();
    private static readonly (JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions) RpcMarshalableInterfaceDefaultOptions = (new JsonRpcProxyOptions(), new JsonRpcTargetOptions { NotifyClientOfEvents = false, DisposeOnDisconnect = true });
    private static readonly MethodInfo ReleaseMarshaledObjectMethodInfo = typeof(MessageFormatterRpcMarshaledContextTracker).GetMethod(nameof(ReleaseMarshaledObject), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly ConcurrentDictionary<Type, RpcMarshalableOptionalInterfaceAttribute[]> MarshalableOptionalInterfaces = new ConcurrentDictionary<Type, RpcMarshalableOptionalInterfaceAttribute[]>();

    private readonly Dictionary<long, (IRpcMarshaledContext<object> Context, IDisposable Revert)> marshaledObjects = new Dictionary<long, (IRpcMarshaledContext<object> Context, IDisposable Revert)>();
    private readonly JsonRpc jsonRpc;
    private readonly IJsonRpcFormatterState formatterState;
    private long nextUniqueHandle;

    /// <summary>
    /// A map of outbound request IDs to handles that are keys in the <see cref="marshaledObjects"/> dictionary.
    /// </summary>
    /// <remarks>
    /// This collection is used to avoid memory leaks when marshaled objects have already been converted to a token for an outbound *request*
    /// and the request ends up not being transmitted for any reason.
    /// It will only contain the data until the request is either aborted or a response is received.
    /// </remarks>
    private ImmutableDictionary<RequestId, ImmutableList<(long Handle, bool CallScoped)>> outboundRequestIdMarshalMap = ImmutableDictionary<RequestId, ImmutableList<(long Handle, bool CallScoped)>>.Empty;

    internal MessageFormatterRpcMarshaledContextTracker(JsonRpc jsonRpc, IJsonRpcFormatterState formatterState)
    {
        this.jsonRpc = jsonRpc;
        this.formatterState = formatterState;

        this.jsonRpc.AddLocalRpcMethod("$/releaseMarshaledObject", ReleaseMarshaledObjectMethodInfo, this);

        // We don't offer a way to remove these handlers because this object should has a lifetime closely tied to the JsonRpc object anyway.
        IJsonRpcFormatterCallbacks callbacks = jsonRpc;
        callbacks.RequestTransmissionAborted += (s, e) => this.CleanUpOutboundResources(e.RequestId, successful: false);
        callbacks.ResponseReceived += (s, e) => this.CleanUpOutboundResources(e.RequestId, successful: e.IsSuccessfulResponse);
    }

    /// <summary>
    /// Defines the values of the "__jsonrpc_marshaled" property in the <see cref="MarshalToken"/>.
    /// </summary>
    private enum MarshalMode
    {
        MarshallingProxyBackToOwner = 0,
        MarshallingRealObject = 1,
    }

    internal static bool TryGetMarshalOptionsForType(Type type, [NotNullWhen(true)] out JsonRpcProxyOptions? proxyOptions, [NotNullWhen(true)] out JsonRpcTargetOptions? targetOptions, [NotNullWhen(true)] out RpcMarshalableAttribute? rpcMarshalableAttribute)
    {
        proxyOptions = null;
        targetOptions = null;
        rpcMarshalableAttribute = null;
        if (type.IsInterface is false)
        {
            return false;
        }

        if (MarshaledTypes.TryGetValue(type, out (JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions, RpcMarshalableAttribute Attribute) options))
        {
            proxyOptions = options.ProxyOptions;
            targetOptions = options.TargetOptions;
            rpcMarshalableAttribute = options.Attribute;
            return true;
        }

        foreach ((Type implicitlyMarshaledType, JsonRpcProxyOptions typeProxyOptions, JsonRpcTargetOptions typeTargetOptions, RpcMarshalableAttribute attribute) in ImplicitlyMarshaledTypes)
        {
            if (implicitlyMarshaledType == type ||
                (implicitlyMarshaledType.IsGenericTypeDefinition &&
                 type.IsConstructedGenericType &&
                 implicitlyMarshaledType == type.GetGenericTypeDefinition()))
            {
                proxyOptions = typeProxyOptions;
                targetOptions = typeTargetOptions;
                rpcMarshalableAttribute = attribute;
                MarshaledTypes.TryAdd(type, (proxyOptions, targetOptions, rpcMarshalableAttribute));
                return true;
            }
        }

        if (type.GetCustomAttribute<RpcMarshalableAttribute>() is RpcMarshalableAttribute marshalableAttribute)
        {
            ValidateMarshalableInterface(type, marshalableAttribute);

            proxyOptions = RpcMarshalableInterfaceDefaultOptions.ProxyOptions;
            targetOptions = RpcMarshalableInterfaceDefaultOptions.TargetOptions;
            rpcMarshalableAttribute = marshalableAttribute;
            MarshaledTypes.TryAdd(type, (proxyOptions, targetOptions, rpcMarshalableAttribute));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the cached list of <see cref="RpcMarshalableOptionalInterfaceAttribute"/> applied to
    /// <paramref name="declaredType"/>.
    /// </summary>
    /// <param name="declaredType">The type to get attributes from.</param>
    /// <param name="rpcMarshalableAttribute">The attribute that appears on the declared type.</param>
    /// <returns>The list of <see cref="RpcMarshalableOptionalInterfaceAttribute"/> applied to
    /// <paramref name="declaredType"/>.</returns>
    /// <exception cref="NotSupportedException">If an invalid set of
    /// <see cref="RpcMarshalableOptionalInterfaceAttribute"/> attributes are applied to
    /// <paramref name="declaredType"/>. This could happen if
    /// <see cref="RpcMarshalableOptionalInterfaceAttribute.OptionalInterface"/> or
    /// <see cref="RpcMarshalableOptionalInterfaceAttribute.OptionalInterfaceCode"/> values are duplicated, or if an
    /// optional interface is not marked with <see cref="RpcMarshalableAttribute"/> or it is not a valid marshalable
    /// interface.</exception>
    internal static RpcMarshalableOptionalInterfaceAttribute[] GetMarshalableOptionalInterfaces(Type declaredType, RpcMarshalableAttribute rpcMarshalableAttribute)
    {
        return MarshalableOptionalInterfaces.GetOrAdd(declaredType, declaredType =>
        {
            RpcMarshalableOptionalInterfaceAttribute[] attributes = declaredType.GetCustomAttributes<RpcMarshalableOptionalInterfaceAttribute>(inherit: false).ToArray();
            if (attributes.Select(a => a.OptionalInterfaceCode).Distinct().Count() != attributes.Length)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.RpcMarshalableDuplicatedOptionalInterfaceCode, declaredType.FullName));
            }

            if (attributes.Select(a => a.OptionalInterface).Distinct().Count() != attributes.Length)
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.RpcMarshalableDuplicatedOptionalInterface, declaredType.FullName));
            }

            foreach (RpcMarshalableOptionalInterfaceAttribute attribute in attributes)
            {
                if (attribute.OptionalInterface.GetCustomAttribute<RpcMarshalableAttribute>() is null)
                {
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.RpcMarshalableOptionalInterfaceMustBeMarshalable, attribute.OptionalInterface.FullName));
                }

                // We pass in the declared interface's own attribute rather than the attribute that appears on the optional interface.
                // Only one attribute can control the policy for this marshaled object.
                ValidateMarshalableInterface(attribute.OptionalInterface, rpcMarshalableAttribute);
            }

            return attributes;
        });
    }

    /// <summary>
    /// Prepares a local object to be marshaled over the wire.
    /// </summary>
    /// <param name="marshaledObject">The object to be exposed over RPC.</param>
    /// <param name="options"><inheritdoc cref="RpcMarshaledContext{T}(T, JsonRpcTargetOptions)" path="/param[@name='options']"/></param>
    /// <param name="declaredType">The marshalable interface type of <paramref name="marshaledObject"/> as declared in the RPC contract.</param>
    /// <param name="rpcMarshalableAttribute">The attribute that defines certain options that control which marshaling rules will be followed.</param>
    /// <returns>A token to be serialized so the remote party can invoke methods on the marshaled object.</returns>
    internal MarshalToken GetToken(object marshaledObject, JsonRpcTargetOptions options, Type declaredType, RpcMarshalableAttribute rpcMarshalableAttribute)
    {
        if (this.formatterState.SerializingMessageWithId.IsEmpty)
        {
            throw new NotSupportedException(Resources.MarshaledObjectInNotificationError);
        }

        if (rpcMarshalableAttribute.CallScopedLifetime && !this.formatterState.SerializingRequest)
        {
            throw new NotSupportedException(Resources.CallScopedMarshaledObjectInReturnValueNotAllowed);
        }

        if (marshaledObject is IJsonRpcClientProxyInternal { MarshaledObjectHandle: not null } proxy)
        {
            // Supporting passing of a marshaled object over RPC requires that we:
            // 1. Distinguish passing it back to its original owner vs. a 3rd party over an independent RPC connection.
            // 2. If back to the original owner, we need to reuse the same handle and pass other data so the receiver recognizes this case.
            if (proxy.JsonRpc != this.jsonRpc)
            {
                throw new NotSupportedException("Forwarding an RPC marshaled object to a 3rd party is not supported.");
            }

            return new MarshalToken
            {
                Handle = proxy.MarshaledObjectHandle.Value,
                Marshaled = (int)MarshalMode.MarshallingProxyBackToOwner,
            };
        }

        long handle = this.nextUniqueHandle++;

        IRpcMarshaledContext<object> context = JsonRpc.MarshalWithControlledLifetime(declaredType, marshaledObject, options);

        RpcTargetInfo.RevertAddLocalRpcTarget? revert = this.jsonRpc.AddLocalRpcTargetInternal(
            declaredType,
            context.Proxy,
            new JsonRpcTargetOptions(context.JsonRpcTargetOptions)
            {
                NotifyClientOfEvents = false, // We don't support this yet.
                MethodNameTransform = mn => Invariant($"$/invokeProxy/{handle}/{context.JsonRpcTargetOptions.MethodNameTransform?.Invoke(mn) ?? mn}"),
                DisposeOnDisconnect = !rpcMarshalableAttribute.CallScopedLifetime,
            },
            requestRevertOption: true);
        Assumes.NotNull(revert);

        Type objectType = marshaledObject.GetType();
        List<int>? optionalInterfacesCodes = null;
        foreach (RpcMarshalableOptionalInterfaceAttribute attribute in GetMarshalableOptionalInterfaces(declaredType, rpcMarshalableAttribute))
        {
            if (attribute.OptionalInterface.IsAssignableFrom(objectType))
            {
                optionalInterfacesCodes ??= new();
                optionalInterfacesCodes.Add(attribute.OptionalInterfaceCode);

                this.jsonRpc.AddRpcInterfaceToTargetInternal(
                    attribute.OptionalInterface,
                    context.Proxy,
                    new JsonRpcTargetOptions(context.JsonRpcTargetOptions)
                    {
                        NotifyClientOfEvents = false, // We don't support this yet.
                        MethodNameTransform = mn => Invariant($"$/invokeProxy/{handle}/{attribute.OptionalInterfaceCode}.{context.JsonRpcTargetOptions.MethodNameTransform?.Invoke(mn) ?? mn}"),
                    },
                    revert);
            }
        }

        lock (this.marshaledObjects)
        {
            this.marshaledObjects.Add(handle, (context, revert));
        }

        if (this.formatterState.SerializingRequest)
        {
            ImmutableInterlocked.AddOrUpdate(
                ref this.outboundRequestIdMarshalMap,
                this.formatterState.SerializingMessageWithId,
                ImmutableList.Create((handle, rpcMarshalableAttribute.CallScopedLifetime)),
                (key, value) => value.Add((handle, rpcMarshalableAttribute.CallScopedLifetime)));
        }

        string? lifetime = rpcMarshalableAttribute.CallScopedLifetime ? MarshalLifetime.Call : null;
        return new MarshalToken((int)MarshalMode.MarshallingRealObject, handle, lifetime, optionalInterfacesCodes?.ToArray());
    }

    /// <summary>
    /// Creates a proxy for a remote object.
    /// </summary>
    /// <param name="interfaceType">The interface the proxy must implement.</param>
    /// <param name="token">The token received from the remote party that includes the handle to the remote object.</param>
    /// <param name="options">The options to feed into proxy generation.</param>
    /// <returns>The generated proxy, or <see langword="null"/> if <paramref name="token"/> is null.</returns>
    [return: NotNullIfNotNull("token")]
    internal object? GetObject(Type interfaceType, MarshalToken? token, JsonRpcProxyOptions options)
    {
        if (token is null)
        {
            return null;
        }

        if ((MarshalMode)token.Value.Marshaled == MarshalMode.MarshallingProxyBackToOwner)
        {
            lock (this.marshaledObjects)
            {
                if (this.marshaledObjects.TryGetValue(token.Value.Handle, out (IRpcMarshaledContext<object> Context, IDisposable Revert) marshaled))
                {
                    return marshaled.Context.Proxy;
                }
            }

            throw new ProtocolViolationException("Marshaled object \"returned\" with an unrecognized handle.");
        }

        RpcMarshalableAttribute synthesizedAttribute = new()
        {
            CallScopedLifetime = token.Value.Lifetime == MarshalLifetime.Call,
        };
        List<(Type Type, int Code)>? optionalInterfaces = null;
        if (token.Value.OptionalInterfacesCodes?.Length > 0)
        {
            // We ignore unknown optional interface codes
            foreach (int optionalInterfacesCode in token.Value.OptionalInterfacesCodes.Distinct())
            {
                foreach (RpcMarshalableOptionalInterfaceAttribute attribute in GetMarshalableOptionalInterfaces(interfaceType, synthesizedAttribute))
                {
                    if (attribute.OptionalInterfaceCode == optionalInterfacesCode)
                    {
                        optionalInterfaces ??= new();
                        optionalInterfaces.Add((attribute.OptionalInterface.GetTypeInfo(), attribute.OptionalInterfaceCode));
                        break;
                    }
                }
            }
        }

        // CONSIDER: If we ever support arbitrary RPC interfaces, we'd need to consider how events on those interfaces would work.
        object result = this.jsonRpc.Attach(
            interfaceType,
            optionalInterfaces?.ToArray(),
            new JsonRpcProxyOptions(options)
            {
                MethodNameTransform = mn => Invariant($"$/invokeProxy/{token.Value.Handle}/{options.MethodNameTransform(mn)}"),
                OnDispose = token.Value.Lifetime == MarshalLifetime.Call ? null : delegate
                {
                    // Only forward the Dispose call if the marshaled interface derives from IDisposable.
                    if (typeof(IDisposable).IsAssignableFrom(interfaceType))
                    {
                        this.jsonRpc.NotifyAsync(Invariant($"$/invokeProxy/{token.Value.Handle}/{options.MethodNameTransform(nameof(IDisposable.Dispose))}")).Forget();
                    }

                    this.jsonRpc.NotifyWithParameterObjectAsync("$/releaseMarshaledObject", new { handle = token.Value.Handle, ownedBySender = false }).Forget();
                },
            },
            token.Value.Handle);
        if (options.OnProxyConstructed is object)
        {
            options.OnProxyConstructed((IJsonRpcClientProxyInternal)result);
        }

        return result;
    }

    /// <summary>
    /// Called near the conclusion of a successful outbound request (i.e. when processing the received response)
    /// to extend the lifetime of call-scoped marshaled objects.
    /// </summary>
    /// <param name="requestId">The ID of the request to extend.</param>
    /// <returns>A value that may be disposed of to finally release the resources bound up with the request.</returns>
    /// <remarks>
    /// This is useful to keep call-scoped arguments alive while the server's <see cref="IAsyncEnumerable{T}"/> result
    /// is still active, suggesting the server may still need access to the arguments passed to it.
    /// </remarks>
    internal IDisposable? OutboundCleanupDeferral(RequestId requestId)
    {
        // Remove the handles from the map so that they don't get cleaned up when the request is completed.
        if (ImmutableInterlocked.TryRemove(ref this.outboundRequestIdMarshalMap, requestId, out ImmutableList<(long Handle, bool CallScoped)>? handles))
        {
            return new DisposableAction(delegate
            {
                // Add the handles back to the map so that they get cleaned up in the normal way, which we then invoke immediately,
                // since the time to clean them up normally has presumably already passed.
                Assumes.True(ImmutableInterlocked.TryAdd(ref this.outboundRequestIdMarshalMap, requestId, handles));
                this.CleanUpOutboundResources(requestId, successful: true);
            });
        }
        else
        {
            // Nothing to defer.
            return null;
        }
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> if <paramref name="type"/> is not a valid marshalable interface.
    /// This method doesn't validate that <paramref name="type"/> has the <see cref="RpcMarshalableAttribute"/>
    /// attribute.
    /// </summary>
    /// <param name="type">The interface <see cref="Type"/> to validate.</param>
    /// <param name="attribute">The attribute that appears on the interface.</param>
    /// <exception cref="NotSupportedException">When <paramref name="type"/> is not a valid marshalable interface: this
    /// can happen if <paramref name="type"/> has properties, events or it is not disposable.</exception>
    private static void ValidateMarshalableInterface(Type type, RpcMarshalableAttribute attribute)
    {
        // We only require marshalable interfaces to derive from IDisposable when they are not call-scoped.
        if (attribute.CallScopedLifetime && !typeof(IDisposable).IsAssignableFrom(type))
        {
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.MarshalableInterfaceNotDisposable, type.FullName));
        }

        if (type.GetEvents().Length > 0)
        {
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.MarshalableInterfaceHasEvents, type.FullName));
        }

        if (type.GetProperties().Length > 0)
        {
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.MarshalableInterfaceHasProperties, type.FullName));
        }
    }

    /// <summary>
    /// Releases memory associated with marshaled objects.
    /// </summary>
    /// <param name="handle">The handle to the object as created by the <see cref="GetToken(object, JsonRpcTargetOptions, Type, RpcMarshalableAttribute)"/> method.</param>
    /// <param name="ownedBySender"><see langword="true"/> if the <paramref name="handle"/> was created by (and thus the original object owned by) the remote party; <see langword="false"/> if the token and object was created locally.</param>
    private void ReleaseMarshaledObject(long handle, bool ownedBySender)
    {
        lock (this.marshaledObjects)
        {
            if (this.marshaledObjects.TryGetValue(handle, out (IRpcMarshaledContext<object> Context, IDisposable Revert) info))
            {
                this.marshaledObjects.Remove(handle);
                info.Revert.Dispose();

                // If/when we support exposing the Context object, it may become relevant to dispose of it.
                ////info.Context.Dispose();
            }
        }
    }

    private void CleanUpOutboundResources(RequestId requestId, bool successful)
    {
        if (ImmutableInterlocked.TryRemove(ref this.outboundRequestIdMarshalMap, requestId, out ImmutableList<(long Handle, bool CallScoped)>? handles))
        {
            foreach ((long handle, bool callScoped) in handles)
            {
                // For explicit lifetime objects, we only kill the marshaled objects if the server threw an error.
                // Successful responses make it the responsibility of the client/server to terminate the marshaled connection.
                // But for call-scoped objects, we always release them when the outbound request is complete, by error or result.
                if (callScoped || !successful)
                {
                    // We use "ownedBySender: false" because the method we're calling is accustomed to the perspective being the "other" party.
                    this.ReleaseMarshaledObject(handle, ownedBySender: false);
                }
            }
        }
    }

    /// <summary>
    /// A token that represents a marshaled object.
    /// </summary>
    [DataContract]
    [GenerateShape]
    internal partial struct MarshalToken
    {
        [MessagePack.SerializationConstructor]
        [ConstructorShape]
#pragma warning disable SA1313 // Parameter names should begin with lower-case letter
        public MarshalToken(int __jsonrpc_marshaled, long handle, string? lifetime, int[]? optionalInterfaces)
#pragma warning restore SA1313 // Parameter names should begin with lower-case letter
        {
            this.Marshaled = __jsonrpc_marshaled;
            this.Handle = handle;
            this.Lifetime = lifetime;
            this.OptionalInterfacesCodes = optionalInterfaces;
        }

        [DataMember(Name = "__jsonrpc_marshaled", IsRequired = true)]
        [STJ.JsonPropertyName("__jsonrpc_marshaled"), STJ.JsonRequired]
        [PropertyShape(Name = "__jsonrpc_marshaled")]
        public int Marshaled { get; set; }

        [DataMember(Name = "handle", IsRequired = true)]
        [STJ.JsonPropertyName("handle"), STJ.JsonRequired]
        [PropertyShape(Name = "handle")]
        public long Handle { get; set; }

        [DataMember(Name = "lifetime", EmitDefaultValue = false)]
        [STJ.JsonPropertyName("lifetime"), STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
        [PropertyShape(Name = "lifetime")]
        public string? Lifetime { get; set; }

        [DataMember(Name = "optionalInterfaces", EmitDefaultValue = false)]
        [STJ.JsonPropertyName("optionalInterfaces"), STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
        [PropertyShape(Name = "optionalInterfaces")]
        public int[]? OptionalInterfacesCodes { get; set; }
    }

    /// <summary>
    /// Defines the values of the "lifetime" property in the <see cref="MarshalToken"/>.
    /// </summary>
    private static class MarshalLifetime
    {
        /// <summary>
        /// The marshaled object may only be invoked until the containing RPC call completes. This value is only allowed when used within a JSON-RPC argument.
        /// No explicit release using `$/releaseMarshaledObject` is required.
        /// </summary>
        internal const string Call = "call";

        /// <summary>
        /// The marshaled object may be invoked until `$/releaseMarshaledObject` releases it. This is the default behavior when the `lifetime` property is omitted.
        /// </summary>
        internal const string Explicit = "explicit";
    }
}
