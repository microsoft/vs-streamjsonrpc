// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.Threading;

namespace StreamJsonRpc.Reflection;

public class RpcTargetMetadata
{
    private const string ImpliedMethodNameAsyncSuffix = "Async";
    private static readonly ConcurrentDictionary<Type, IEventHandlerFactory> EventHandlerFactories = [];
    private static readonly ConcurrentDictionary<Type, RpcTargetMetadata> Interfaces = [];
    private static readonly ConcurrentDictionary<Type, RpcTargetMetadata> PublicClass = [];
    private static readonly ConcurrentDictionary<Type, RpcTargetMetadata> NonPublicClass = [];
    private static readonly MethodInfo RegisterEventArgsMethodInfo = typeof(RpcTargetMetadata).GetMethod(nameof(RegisterEventArgs), BindingFlags.Public | BindingFlags.Static) ?? throw Assumes.NotReachable();
    private static Action<Type>? dynamicEventHandlerFactoryRegistration;

    public delegate Delegate CreateEventHandlerDelegate(JsonRpc rpc, string eventName);

    private interface IEventHandlerFactory
    {
        /// <summary>
        /// Creates an event handler for the specified event.
        /// </summary>
        /// <param name="rpc">The JSON-RPC instance to use for sending notifications.</param>
        /// <param name="eventName">The name of the event to create a handler for.</param>
        /// <returns>A delegate that can be used as an event handler.</returns>
        Delegate CreateEventHandler(JsonRpc rpc, string eventName, Type delegateType);
    }

    /// <summary>
    /// Gets the methods that can be invoked on this RPC target.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<TargetMethodMetadata>> Methods { get; init; }

    /// <summary>
    /// Gets the list of events that can be raised by this RPC target.
    /// </summary>
    public required IReadOnlyList<EventMetadata> Events { get; init; }

    public required Type TargetType { get; init; }

    /// <summary>
    /// Enables dynamic generation of event handlers for <see cref="EventHandler{TEventArgs}"/> delegates
    /// where <c>TEventArgs</c> is a value type.
    /// </summary>
    /// <remarks>
    /// This method is not safe to use in NativeAOT applications.
    /// Such applications should either call <see cref="RegisterEventArgs{TEventArgs}"/> directly for each value-type type argument,
    /// or rely on source generation to do so.
    /// </remarks>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    public static void EnableDynamicEventHandlerCreation()
    {
        dynamicEventHandlerFactoryRegistration ??= (type) =>
        {
            RegisterEventArgsMethodInfo.MakeGenericMethod(type).Invoke(null, null);
        };
    }

#if !NET10_0_OR_GREATER
    [SuppressMessage("Trimming", "IL2062:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "We use the All link demand on rpcContract, so results of GetInterfaces() should work. See https://github.com/dotnet/linker/issues/1731")]
    [SuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "We use the All link demand on rpcContract, so results of GetInterfaces() should work.")]
#endif
    public static RpcTargetMetadata FromInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type rpcContract)
    {
        Requires.NotNull(rpcContract);
        Requires.Argument(rpcContract.IsInterface, nameof(rpcContract), "The type must be an interface.");

        if (Interfaces.TryGetValue(rpcContract, out RpcTargetMetadata? result))
        {
            return result;
        }

        Builder builder = new(rpcContract);
        WalkInterface(rpcContract);
        foreach (Type baseInterfaces in rpcContract.GetInterfaces())
        {
            WalkInterface(baseInterfaces);
        }

        void WalkInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
        {
            AddMethods(builder, iface.GetMethods(BindingFlags.Public | BindingFlags.Instance));
            AddEvents(builder, iface.GetEvents(BindingFlags.Public | BindingFlags.Instance));
        }

        result = builder.ToImmutable();
        return Interfaces.TryAdd(rpcContract, result) ? result : Interfaces[rpcContract];
    }

    public static RpcTargetMetadata FromInterfaces(InterfaceCollection interfaces)
    {
        Requires.NotNull(interfaces);
        IReadOnlyList<Type> missingInterfaces = interfaces.GetMissingInterfacesFromSet();
        Requires.Argument(missingInterfaces is [], nameof(interfaces), $"The interface collection is missing interfaces that the primary interface derives from: {string.Join(", ", missingInterfaces.Select(t => t.FullName))}.");

        if (Interfaces.TryGetValue(interfaces.PrimaryInterface, out RpcTargetMetadata? result))
        {
            // If we already have metadata for the primary interface, return it.
            return result;
        }

        Builder builder = new(interfaces.PrimaryInterface);
        for (int i = 0; i < interfaces.Count; i++)
        {
            WalkInterface(interfaces[i]);
        }

        void WalkInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
        {
            AddMethods(builder, iface.GetMethods(BindingFlags.Public | BindingFlags.Instance));
            AddEvents(builder, iface.GetEvents(BindingFlags.Public | BindingFlags.Instance));
        }

        result = builder.ToImmutable();

        // It's safe to store and share the result because we confirmed that InterfaceCollection is complete,
        // and the collection itself ensures that it does not have an excess of interfaces.
        return Interfaces.TryAdd(interfaces[0], result) ? result : Interfaces[interfaces[0]];
    }

    public static RpcTargetMetadata FromClass([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)] Type classType)
    {
        Requires.NotNull(classType);
        Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");

        if (PublicClass.TryGetValue(classType, out RpcTargetMetadata? result))
        {
            return result;
        }

        Builder builder = new(classType);
        AddMethods(builder, classType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        AddEvents(builder, classType.GetEvents(BindingFlags.Public | BindingFlags.Instance));
        result = builder.ToImmutable();

        return PublicClass.TryAdd(classType, result) ? result : PublicClass[classType];
    }

    public static RpcTargetMetadata FromClassNonPublic([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicEvents | DynamicallyAccessedMemberTypes.Interfaces)] Type classType)
    {
        Requires.NotNull(classType);
        Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");

        if (NonPublicClass.TryGetValue(classType, out RpcTargetMetadata? result))
        {
            return result;
        }

        Builder builder = new(classType);
        AddMethods(builder, classType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
        AddEvents(builder, classType.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        result = builder.ToImmutable();
        return NonPublicClass.TryAdd(classType, result) ? result : NonPublicClass[classType];
    }

    /// <summary>
    /// Creates an event handler factory that supports <see cref="EventHandler{TEventArgs}"/> for a given <typeparamref name="TEventArgs"/>.
    /// </summary>
    /// <typeparam name="TEventArgs">
    /// The type argument used in <see cref="EventHandler{TEventArgs}"/>.
    /// Only structs are supported because only value types need registration. Reference types work without registration.
    /// </typeparam>
    public static void RegisterEventArgs<TEventArgs>()
        where TEventArgs : struct => EventHandlerFactories.TryAdd(typeof(TEventArgs), new EventHandlerFactory<TEventArgs>());

    private static void AddMethods(Builder builder, IEnumerable<MethodInfo> methods)
    {
        foreach (MethodInfo method in methods)
        {
            TryAddCandidateMethod(builder, method);
        }
    }

    private static bool TryAddCandidateMethod(Builder builder, MethodInfo method)
    {
        if (method.IsSpecialName || method.IsConstructor || method.DeclaringType == typeof(object))
        {
            return false;
        }

        JsonRpcIgnoreAttribute? ignoreAttribute = FindMethodAttribute<JsonRpcIgnoreAttribute>(builder, method);
        JsonRpcMethodAttribute? methodAttribute = FindMethodAttribute<JsonRpcMethodAttribute>(builder, method);

        if (ignoreAttribute is not null)
        {
            if (methodAttribute is not null)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Resources.JsonRpcMethodAndIgnoreAttributesFound, method.Name));
            }

            return false;
        }

        var methodMetadata = TargetMethodMetadata.From(method, methodAttribute);

        if (!builder.Methods.TryGetValue(methodMetadata.Name, out List<TargetMethodMetadata>? methodList))
        {
            builder.Methods[methodMetadata.Name] = methodList = [];
        }

        methodList.Add(methodMetadata);
        return true;
    }

    private static void AddEvents(Builder builder, IEnumerable<EventInfo> events)
    {
        foreach (EventInfo @event in events)
        {
            TryAddCandidateEvent(builder, @event);
        }
    }

    private static bool TryAddCandidateEvent(Builder builder, EventInfo @event)
    {
        if (@event.EventHandlerType is null)
        {
            return false;
        }

        CreateEventHandlerDelegate? createEventHandler;
        if (@event.EventHandlerType == typeof(EventHandler))
        {
            createEventHandler = (rpc, eventName) => new EventHandler((object? sender, EventArgs args) => rpc.NotifyAsync(eventName, [args]).Forget());
        }
        else if (@event.EventHandlerType.IsGenericType && @event.EventHandlerType.GetGenericTypeDefinition() == typeof(EventHandler<>) &&
            @event.EventHandlerType.GetGenericArguments() is [{ } argType])
        {
            createEventHandler = CreateEventDelegate(argType);
        }
        else if (GetParameters(@event) is [{ ParameterType: Type senderType }, { ParameterType: Type argType2 }] && senderType == typeof(object))
        {
            createEventHandler = CreateEventDelegate(argType2);
        }
        else
        {
            // We don't support this delegate type.
            throw new NotSupportedException($"Only EventHandler and EventHandler<T> delegates are supported for RPC, but {@event.DeclaringType}.{@event.Name} has unsupported type {@event.EventHandlerType}.");
        }

        builder.Events.Add(new EventMetadata
        {
            Event = @event,
            Name = @event.Name,
            EventHandlerType = @event.EventHandlerType,
            CreateEventHandler = createEventHandler,
        });
        return true;

        CreateEventHandlerDelegate CreateEventDelegate(Type argType)
        {
            if (!argType.IsValueType)
            {
                return (rpc, eventName) =>
                {
                    Type[] argTypes = [argType];
                    Delegate d = (object? sender, object? args) => rpc.NotifyAsync(eventName, [args], [argType]).Forget();
                    return Delegate.CreateDelegate(@event.EventHandlerType, d.Target, d.Method);
                };
            }
            else if (EventHandlerFactories.TryGetValue(argType, out IEventHandlerFactory? factory))
            {
                return (jsonRpc, eventName) => factory.CreateEventHandler(jsonRpc, eventName, @event.EventHandlerType);
            }
            else
            {
                if (dynamicEventHandlerFactoryRegistration is not null)
                {
                    dynamicEventHandlerFactoryRegistration(argType);
                    Assumes.True(EventHandlerFactories.TryGetValue(argType, out factory));
                    return (jsonRpc, eventName) => factory.CreateEventHandler(jsonRpc, eventName, @event.EventHandlerType);
                }

                // We don't have a factory registered for this value type.
                throw new NotSupportedException($"{@event.DeclaringType}.{@event.Name} event uses {argType} as its second parameter. Structs used as event args must be registered beforehand using {nameof(RpcTargetMetadata)}.{nameof(RegisterEventArgs)}<T>().");
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "False positive: https://github.com/dotnet/runtime/issues/114113")]
        static ParameterInfo[] GetParameters(EventInfo eventInfo) =>
            eventInfo.EventHandlerType!.GetTypeInfo().GetMethod("Invoke")!.GetParameters();
    }

    private static T? FindMethodAttribute<T>(Builder builder, MethodInfo method)
        where T : Attribute
    {
        if (method.GetCustomAttribute<T>() is T attribute)
        {
            return attribute;
        }

        for (int i = 0; i < builder.InterfaceMaps.Length; i++)
        {
            InterfaceMapping map = builder.InterfaceMaps.Span[i];
            int methodIndex = Array.IndexOf(map.TargetMethods, method);
            if (methodIndex >= 0 && map.InterfaceMethods[methodIndex].GetCustomAttribute<T>() is T inheritedAttribute)
            {
                return inheritedAttribute;
            }
        }

        return null;
    }

    public class InterfaceCollection : IEnumerable<Type>
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)]
        private Type primaryInterface;

        private List<RpcTargetInterface> interfaces;

        public InterfaceCollection([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)] Type primaryInterface)
        {
            Requires.NotNull(primaryInterface);
            Requires.Argument(primaryInterface.IsInterface, nameof(primaryInterface), "The type must be an interface.");

            this.primaryInterface = primaryInterface;
            this.interfaces = [new(primaryInterface)];
        }

        internal int Count => this.interfaces.Count;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)]
        internal Type PrimaryInterface => this.primaryInterface;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)]
        internal Type this[int index] => this.interfaces[index].Interface;

        public void Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
        {
            Requires.NotNull(iface);
            Requires.Argument(iface.IsInterface && iface.IsAssignableFrom(this.interfaces[0].Interface), nameof(iface), "This type must be an interface from which the primary interface derives.");

            this.interfaces.Add(new RpcTargetInterface(iface));
        }

        public IEnumerator<Type> GetEnumerator() => this.interfaces.Select(t => t.Interface).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        internal IReadOnlyList<Type> GetMissingInterfacesFromSet()
        {
            List<Type>? missing = null;

            // Verify that all interfaces are present.
            foreach (Type derivedFrom in this.primaryInterface.GetInterfaces())
            {
                bool found = false;
                for (int i = 1; i < this.interfaces.Count; i++)
                {
                    if (this.interfaces[i].Interface == derivedFrom)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    missing ??= [];
                    missing.Add(derivedFrom);
                }
            }

            return missing ?? [];
        }
    }

    internal struct RpcTargetInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)]
        public Type Interface => iface;
    }

    public class EventMetadata
    {
        public required EventInfo Event { get; init; }

        public required string Name { get; init; }

        public required Type EventHandlerType { get; init; }

        public required CreateEventHandlerDelegate CreateEventHandler { get; init; }
    }

    [DebuggerDisplay($"{{{nameof(DebuggerDisplay)},nq}}")]
    public class TargetMethodMetadata
    {
        private ParameterInfo[]? parameters;

        public required MethodInfo Method { get; init; }

        public required string Name { get; init; }

        public required JsonRpcMethodAttribute? Attribute { get; init; }

        internal IReadOnlyList<ParameterInfo> Parameters => this.parameters ??= this.Method.GetParameters() ?? [];

        internal ReadOnlyMemory<ParameterInfo> ParametersMemory => (ParameterInfo[])this.Parameters;

        internal bool IsPublic => this.Method.IsPublic;

        internal int RequiredParamCount => this.Parameters.Count(pi => !pi.IsOptional && pi.ParameterType != typeof(CancellationToken));

        internal int TotalParamCountExcludingCancellationToken => this.HasCancellationTokenParameter ? this.Parameters.Count - 1 : this.Parameters.Count;

        internal bool HasCancellationTokenParameter => this.Parameters is [.., { ParameterType: { } type }] && type == typeof(CancellationToken);

        internal bool HasOutOrRefParameters => this.Parameters.Any(pi => pi.IsOut || pi.ParameterType.IsByRef);

        [ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{this.Method.DeclaringType}.{this.Name}({string.Join(", ", this.Parameters.Select(p => p.ParameterType.Name))})";

        public override string ToString() => this.DebuggerDisplay;

        internal bool EqualSignature(TargetMethodMetadata other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (this.TotalParamCountExcludingCancellationToken != other.TotalParamCountExcludingCancellationToken)
            {
                return false;
            }

            for (int i = 0; i < this.TotalParamCountExcludingCancellationToken; i++)
            {
                if (this.Parameters[i].ParameterType != other.Parameters[i].ParameterType)
                {
                    return false;
                }
            }

            return true;
        }

        internal static TargetMethodMetadata From(MethodInfo method, JsonRpcMethodAttribute? attribute)
            => new()
            {
                Method = method,
                Name = attribute?.Name ?? method.Name,
                Attribute = attribute,
            };

        internal bool MatchesParametersExcludingCancellationToken(ReadOnlySpan<ParameterInfo> parameters)
        {
            if (this.TotalParamCountExcludingCancellationToken == parameters.Length)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType != this.Parameters[i].ParameterType)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }
    }

    private class EventHandlerFactory<TEventArgs> : IEventHandlerFactory
    {
        public Delegate CreateEventHandler(JsonRpc rpc, string eventName, Type delegateType)
        {
            Type[] argTypes = [typeof(TEventArgs)];
            Delegate d = (object? sender, TEventArgs args) => rpc.NotifyAsync(eventName, [args], argTypes).Forget();
            return d.Method.CreateDelegate(delegateType, d.Target);
        }
    }

    private class Builder([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
    {
        internal ReadOnlyMemory<InterfaceMapping> InterfaceMaps { get; } = GetInterfaceMaps(type);

        private static ReadOnlyMemory<InterfaceMapping> GetInterfaceMaps([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
        {
            if (type.IsInterface)
            {
                return default;
            }

            List<InterfaceMapping> mapping = [];
            foreach (Type iface in type.GetTypeInfo().ImplementedInterfaces)
            {
                mapping.Add(type.GetInterfaceMap(iface));
            }

            return mapping.ToArray();
        }

        internal Dictionary<string, List<TargetMethodMetadata>> Methods { get; } = new(StringComparer.Ordinal);

        internal List<EventMetadata> Events { get; } = [];

        internal RpcTargetMetadata ToImmutable()
        {
            this.GenerateAliases();

            return new RpcTargetMetadata
            {
                TargetType = type,
                Methods = this.Methods.ToImmutableDictionary(kv => kv.Key, kv => (IReadOnlyList<TargetMethodMetadata>)kv.Value.ToArray()),
                Events = [.. this.Events],
            };
        }

        private void GenerateAliases()
        {
            // Create aliases for methods ending in Async that don't have the JsonRpcMethodAttribute,
            // when renaming them would not create overload collisions with the shortened name.
            Dictionary<string, List<TargetMethodMetadata>> aliasedMethods = [];
            foreach ((string name, List<TargetMethodMetadata> overloads) in this.Methods)
            {
                if (name.EndsWith(ImpliedMethodNameAsyncSuffix, StringComparison.Ordinal))
                {
                    string alias = name[..^ImpliedMethodNameAsyncSuffix.Length];
                    if (!this.Methods.ContainsKey(alias))
                    {
                        List<TargetMethodMetadata> implicitlyNamed = [.. overloads.Where(o => o.Attribute?.Name is null)];
                        if (implicitlyNamed.Count > 0)
                        {
                            aliasedMethods.Add(alias, [.. overloads.Where(o => o.Attribute?.Name is null)]);
                        }
                    }
                }
            }

            foreach ((string alias, List<TargetMethodMetadata> overloads) in aliasedMethods)
            {
                this.Methods.Add(alias, overloads);
            }
        }
    }
}
