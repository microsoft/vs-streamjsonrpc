// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using static System.FormattableString;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Tracks objects that get marshaled using the general marshaling protocol.
/// </summary>
internal class MessageFormatterRpcMarshaledContextTracker
{
    private static readonly IReadOnlyCollection<(Type ImplicitlyMarshaledType, JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions)> ImplicitlyMarshaledTypes = new (Type ImplicitlyMarshaledType, JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions)[]
    {
        (typeof(IDisposable), new JsonRpcProxyOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase }, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase }),

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
            new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase }),
    };

    private static readonly ConcurrentDictionary<Type, (JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions)> MarshaledTypes = new ConcurrentDictionary<Type, (JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions)>();
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
    private ImmutableDictionary<RequestId, ImmutableList<long>> outboundRequestIdMarshalMap = ImmutableDictionary<RequestId, ImmutableList<long>>.Empty;

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

    internal static bool TryGetMarshalOptionsForType(Type type, [NotNullWhen(true)] out JsonRpcProxyOptions? proxyOptions, [NotNullWhen(true)] out JsonRpcTargetOptions? targetOptions)
    {
        proxyOptions = null;
        targetOptions = null;
        if (type.IsInterface is false)
        {
            return false;
        }

        if (MarshaledTypes.TryGetValue(type, out (JsonRpcProxyOptions ProxyOptions, JsonRpcTargetOptions TargetOptions) options))
        {
            proxyOptions = options.ProxyOptions;
            targetOptions = options.TargetOptions;
            return true;
        }

        foreach ((Type implicitlyMarshaledType, JsonRpcProxyOptions typeProxyOptions, JsonRpcTargetOptions typeTargetOptions) in ImplicitlyMarshaledTypes)
        {
            if (implicitlyMarshaledType == type ||
                (implicitlyMarshaledType.IsGenericTypeDefinition &&
                 type.IsConstructedGenericType &&
                 implicitlyMarshaledType == type.GetGenericTypeDefinition()))
            {
                proxyOptions = typeProxyOptions;
                targetOptions = typeTargetOptions;
                MarshaledTypes.TryAdd(type, (proxyOptions, targetOptions));
                return true;
            }
        }

        if (type.GetCustomAttribute<RpcMarshalableAttribute>() is not null)
        {
            ValidateMarshalableInterface(type);

            proxyOptions = RpcMarshalableInterfaceDefaultOptions.ProxyOptions;
            targetOptions = RpcMarshalableInterfaceDefaultOptions.TargetOptions;
            MarshaledTypes.TryAdd(type, (proxyOptions, targetOptions));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the cached list of <see cref="RpcMarshalableOptionalInterfaceAttribute"/> applied to
    /// <paramref name="declaredType"/>.
    /// </summary>
    /// <param name="declaredType">The type to get attributes from.</param>
    /// <returns>The list of <see cref="RpcMarshalableOptionalInterfaceAttribute"/> applied to
    /// <paramref name="declaredType"/>.</returns>
    /// <exception cref="NotSupportedException">If an invalid set of
    /// <see cref="RpcMarshalableOptionalInterfaceAttribute"/> attributes are applied to
    /// <paramref name="declaredType"/>. This could happen if
    /// <see cref="RpcMarshalableOptionalInterfaceAttribute.OptionalInterface"/> or
    /// <see cref="RpcMarshalableOptionalInterfaceAttribute.OptionalInterfaceCode"/> values are duplicated, or if an
    /// optional interface is not marked with <see cref="RpcMarshalableAttribute"/> or it is not a valid marshalable
    /// interface.</exception>
    internal static RpcMarshalableOptionalInterfaceAttribute[] GetMarshalableOptionalInterfaces(Type declaredType)
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

                ValidateMarshalableInterface(attribute.OptionalInterface);
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
    /// <returns>A token to be serialized so the remote party can invoke methods on the marshaled object.</returns>
    internal MarshalToken GetToken(object marshaledObject, JsonRpcTargetOptions options, Type declaredType)
    {
        if (this.formatterState.SerializingMessageWithId.IsEmpty)
        {
            throw new NotSupportedException(Resources.MarshaledObjectInNotificationError);
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
            },
            requestRevertOption: true);
        Assumes.NotNull(revert);

        Type objectType = marshaledObject.GetType();
        List<int>? optionalInterfacesCodes = null;
        foreach (RpcMarshalableOptionalInterfaceAttribute attribute in GetMarshalableOptionalInterfaces(declaredType))
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
                ImmutableList.Create(handle),
                (key, value) => value.Add(handle));
        }

        return new MarshalToken((int)MarshalMode.MarshallingRealObject, handle, lifetime: null, optionalInterfacesCodes?.ToArray());
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
            throw new NotSupportedException("Receiving marshaled objects back to the owner is not yet supported.");
        }

        if (token.Value.Lifetime == MarshalLifetime.Call)
        {
            throw new NotSupportedException("Receiving marshaled objects scoped to the lifetime of a single RPC request is not yet supported.");
        }

        List<(TypeInfo Type, int Code)>? optionalInterfaces = null;
        if (token.Value.OptionalInterfacesCodes?.Length > 0)
        {
            // We ignore unknown optional interface codes
            foreach (int optionalInterfacesCode in token.Value.OptionalInterfacesCodes.Distinct())
            {
                foreach (RpcMarshalableOptionalInterfaceAttribute attribute in GetMarshalableOptionalInterfaces(interfaceType))
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
                OnDispose = delegate
                {
                    // Only forward the Dispose call if the marshaled interface derives from IDisposable.
                    if (typeof(IDisposable).IsAssignableFrom(interfaceType))
                    {
                        this.jsonRpc.NotifyAsync(Invariant($"$/invokeProxy/{token.Value.Handle}/{options.MethodNameTransform(nameof(IDisposable.Dispose))}")).Forget();
                    }

                    this.jsonRpc.NotifyWithParameterObjectAsync("$/releaseMarshaledObject", new { handle = token.Value.Handle, ownedBySender = false }).Forget();
                },
            });
        if (options.OnProxyConstructed is object)
        {
            options.OnProxyConstructed((IJsonRpcClientProxyInternal)result);
        }

        return result;
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> if <paramref name="type"/> is not a valid marshalable interface.
    /// This method doesn't validate that <paramref name="type"/> has the <see cref="RpcMarshalableAttribute"/>
    /// attribute.
    /// </summary>
    /// <param name="type">The interface <see cref="Type"/> to validate.</param>
    /// <exception cref="NotSupportedException">When <paramref name="type"/> is not a valid marshalable interface: this
    /// can happen if <paramref name="type"/> has properties, events or it is not disposable.</exception>
    private static void ValidateMarshalableInterface(Type type)
    {
        if (typeof(IDisposable).IsAssignableFrom(type) is false)
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
    /// <param name="handle">The handle to the object as created by the <see cref="GetToken(object, JsonRpcTargetOptions, Type)"/> method.</param>
    /// <param name="ownedBySender"><see langword="true"/> if the <paramref name="handle"/> was created by (and thus the original object owned by) the remote party; <see langword="false"/> if the token and object was created locally.</param>
#pragma warning disable CA1801 // Review unused parameters -- Signature is dicated by protocol extension server method.
    private void ReleaseMarshaledObject(long handle, bool ownedBySender)
#pragma warning restore CA1801 // Review unused parameters
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
        if (ImmutableInterlocked.TryRemove(ref this.outboundRequestIdMarshalMap, requestId, out ImmutableList<long>? handles))
        {
            // Only kill the marshaled objects if the server threw an error.
            // Successful responses make it the responsibility of the client/server to terminate the marshaled connection.
            if (!successful)
            {
                foreach (long handle in handles)
                {
                    // We use "ownedBySender: false" because the method we're calling is accustomed to the perspective being the "other" party.
                    this.ReleaseMarshaledObject(handle, ownedBySender: false);
                }
            }
        }
    }

    [DataContract]
    internal struct MarshalToken
    {
        [MessagePack.SerializationConstructor]
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
        public int Marshaled { get; }

        [DataMember(Name = "handle", IsRequired = true)]
        public long Handle { get; }

        [DataMember(Name = "lifetime", EmitDefaultValue = false)]
        public string? Lifetime { get; }

        [DataMember(Name = "optionalInterfaces", EmitDefaultValue = false)]
        public int[]? OptionalInterfacesCodes { get; }
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
