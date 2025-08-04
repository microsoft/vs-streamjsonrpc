﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.Threading;

namespace StreamJsonRpc;

/// <summary>
/// Describes an RPC target type, which can be an interface or a class.
/// </summary>
public class RpcTargetMetadata
{
    private const string ImpliedMethodNameAsyncSuffix = "Async";
    private static readonly ConcurrentDictionary<Type, IEventHandlerFactory> EventHandlerFactories = [];
    private static readonly ConcurrentDictionary<Type, RpcTargetMetadata> Interfaces = [];
    private static readonly ConcurrentDictionary<Type, RpcTargetMetadata> PublicClass = [];
    private static readonly ConcurrentDictionary<Type, RpcTargetMetadata> NonPublicClass = [];
    private static readonly MethodInfo RegisterEventArgsMethodInfo = typeof(RpcTargetMetadata).GetMethod(nameof(RegisterEventArgs), BindingFlags.Public | BindingFlags.Static) ?? throw Assumes.NotReachable();
    private static Action<Type>? dynamicEventHandlerFactoryRegistration;

    /// <summary>
    /// Represents a method that creates a delegate to handle a specified JSON-RPC event.
    /// </summary>
    /// <param name="rpc">The JSON-RPC connection for which the event handler delegate is being created. Cannot be <see langword="null" />.</param>
    /// <param name="eventName">The name of the event for which to create the handler delegate. Cannot be <see langword="null" /> or empty.</param>
    /// <returns>A delegate instance that handles the specified event for the given JSON-RPC connection.</returns>
    public delegate Delegate CreateEventHandlerDelegate(JsonRpc rpc, string eventName);

    private interface IEventHandlerFactory
    {
        /// <summary>
        /// Creates an event handler for the specified event.
        /// </summary>
        /// <param name="rpc">The JSON-RPC instance to use for sending notifications.</param>
        /// <param name="eventName">The name of the event to create a handler for.</param>
        /// <param name="delegateType">The type of the event/delegate to be returned.</param>
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

    /// <summary>
    /// Gets the type of the RPC target, which can be an interface or a class.
    /// </summary>
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
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "The generic method we construct has no dynamic member access requirements.")]
    public static void EnableDynamicEventHandlerCreation()
    {
        dynamicEventHandlerFactoryRegistration ??= (type) =>
        {
            RegisterEventArgsMethodInfo.MakeGenericMethod(type).Invoke(null, null);
        };
    }

    /// <summary>
    /// Creates an instance of RpcTargetMetadata that describes the specified RPC contract interface.
    /// </summary>
    /// <param name="rpcContract">The interface type that defines the RPC contract. Must not be null and must represent an interface type.</param>
    /// <returns>An <see cref="RpcTargetMetadata"/> instance that provides metadata for the specified RPC contract interface.</returns>
    /// <remarks>
    /// <para>
    /// If metadata for the specified interface has already been created, the existing instance is returned.
    /// Otherwise, a new metadata instance is generated.
    /// This method is typically used to obtain metadata required for dispatching or proxying RPC calls based
    /// on an interface definition.
    /// </para>
    /// <para>
    /// While convenient, this method produces the least trimmable code.
    /// For a smaller trimmed application, use <see cref="FromInterface(InterfaceCollection)" /> instead.
    /// </para>
    /// </remarks>
    public static RpcTargetMetadata FromInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type rpcContract)
    {
        Requires.NotNull(rpcContract);
        Requires.Argument(rpcContract.IsInterface, nameof(rpcContract), "The type must be an interface.");

        return Interfaces.TryGetValue(rpcContract, out RpcTargetMetadata? result)
            ? result
            : FromInterface(InterfaceCollection.Create(rpcContract));
    }

    /// <summary>
    /// Creates metadata describing the RPC target for the specified set of interfaces.
    /// </summary>
    /// <param name="interfaces">
    /// A collection of interfaces, including the primary interface and any interfaces it derives from, to generate
    /// metadata for.
    /// </param>
    /// <returns>An instance of <see cref="RpcTargetMetadata"/> representing the RPC target metadata for the provided interfaces.</returns>
    /// <remarks>
    /// <para>
    /// If metadata for the specified interface has already been created, the existing instance is returned.
    /// Otherwise, a new metadata instance is generated.
    /// This method is typically used to obtain metadata required for dispatching or proxying RPC calls based
    /// on an interface definition.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown if <paramref name="interfaces"/> does not represent all the interfaces that the target interface derives from.</exception>
    public static RpcTargetMetadata FromInterface(InterfaceCollection interfaces)
    {
        Requires.NotNull(interfaces);
        IReadOnlyList<Type> missingInterfaces = interfaces.GetMissingInterfacesFromSet();
        Requires.Argument(missingInterfaces is [], nameof(interfaces), $"The interface collection is missing interfaces that the primary interface derives from: {string.Join(", ", missingInterfaces.Select(t => t.FullName))}.");

        if (Interfaces.TryGetValue(interfaces.PrimaryInterface, out RpcTargetMetadata? result))
        {
            // If we already have metadata for the primary interface, return it.
            return result;
        }

        Builder builder = new(interfaces);
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
        return Interfaces.TryAdd(interfaces.PrimaryInterface, result) ? result : Interfaces[interfaces.PrimaryInterface];
    }

    /// <summary>
    /// Creates a new instance of <see cref="RpcTargetMetadata"/> for the specified class type, including all of its
    /// RPC target members.
    /// </summary>
    /// <param name="classType">The type representing the class for which to generate metadata. Must not be null and should be a concrete class
    /// type.</param>
    /// <returns>An <see cref="RpcTargetMetadata"/> instance containing metadata for the specified class and its interfaces.</returns>
    /// <remarks>
    /// <para>
    /// While convenient, this method produces the least trimmable code.
    /// For a smaller trimmed application, use <see cref="FromClass(Type, ClassAndInterfaces)" /> instead.
    /// </para>
    /// </remarks>
    public static RpcTargetMetadata FromClass([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type classType)
        => FromClass(classType, ClassAndInterfaces.Create(classType));

    /// <summary>
    /// Creates an instance of <see cref="RpcTargetMetadata"/> for the specified class type using the provided metadata.
    /// All public methods and events will be exposed to RPC clients,
    /// unless <see cref="JsonRpcIgnoreAttribute"/> is applied to them.
    /// </summary>
    /// <param name="classType">The class <see cref="Type"/> for which to generate metadata. Must be a non-null class type.</param>
    /// <param name="metadata">The metadata describing the class and its interfaces. Must not be null and must correspond to the specified
    /// class type.</param>
    /// <returns>An <see cref="RpcTargetMetadata"/> instance representing the public methods and events of the specified class.</returns>
    /// <remarks>
    /// If metadata for the specified class type has already been created, the existing instance is returned.
    /// Otherwise, a new instance is generated. If all interfaces implemented by the
    /// class are present in the provided metadata, the resulting instance will be cached for later reuse.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown if the <paramref name="classType"/> does not match the <see cref="ClassAndInterfaces.ClassType"/> in the provided metadata.
    /// </exception>
    public static RpcTargetMetadata FromClass([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type classType, ClassAndInterfaces metadata)
    {
        Requires.NotNull(classType);
        Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");
        Requires.NotNull(metadata);
        Requires.Argument(classType == metadata.ClassType, nameof(metadata), "Metadata must describe the target class.");

        if (PublicClass.TryGetValue(classType, out RpcTargetMetadata? result))
        {
            return result;
        }

        Builder builder = new(metadata);
        AddMethods(builder, classType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        AddEvents(builder, classType.GetEvents(BindingFlags.Public | BindingFlags.Instance));
        result = builder.ToImmutable();

        // If the caller does not have a complete idea of all interfaces that the class implements,
        // we can still return the result, but we will not cache it since that may pollute other users with
        // more complete inputs.
        IReadOnlyList<Type> missingInterfaces = metadata.GetMissingInterfacesFromSet();
        if (missingInterfaces is not [])
        {
            return result;
        }

        return PublicClass.TryAdd(classType, result) ? result : PublicClass[classType];
    }

    /// <summary>
    /// Creates an instance of RpcTargetMetadata for the specified class type, including non-public members
    /// that are not attributed with <see cref="JsonRpcIgnoreAttribute"/>.
    /// </summary>
    /// <param name="classType">The type of the class for which to generate metadata. Must not be null.</param>
    /// <returns>A RpcTargetMetadata instance containing metadata for the specified class type, including its non-public members.</returns>
    /// <remarks>
    /// <para>
    /// While convenient, this method produces the least trimmable code.
    /// For a smaller trimmed application, use <see cref="FromClassNonPublic(Type, ClassAndInterfaces)" /> instead.
    /// </para>
    /// </remarks>
    public static RpcTargetMetadata FromClassNonPublic([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type classType)
        => FromClassNonPublic(classType, ClassAndInterfaces.Create(classType));

    /// <summary>
    /// Creates an instance of <see cref="RpcTargetMetadata"/> for the specified class type using the provided metadata.
    /// All methods and events will be exposed to RPC clients, including non-public members,
    /// unless <see cref="JsonRpcIgnoreAttribute"/> is applied to them.
    /// </summary>
    /// <param name="classType">The class <see cref="Type"/> for which to generate metadata. Must be a non-null class type.</param>
    /// <param name="metadata">The metadata describing the class and its interfaces. Must not be null and must correspond to the specified
    /// class type.</param>
    /// <returns>An <see cref="RpcTargetMetadata"/> instance representing the public methods and events of the specified class.</returns>
    /// <remarks>
    /// If metadata for the specified class type has already been created, the existing instance is returned.
    /// Otherwise, a new instance is generated. If all interfaces implemented by the
    /// class are present in the provided metadata, the resulting instance will be cached for later reuse.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown if the <paramref name="classType"/> does not match the <see cref="ClassAndInterfaces.ClassType"/> in the provided metadata.
    /// </exception>
    public static RpcTargetMetadata FromClassNonPublic([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.NonPublicEvents)] Type classType, ClassAndInterfaces metadata)
    {
        Requires.NotNull(classType);
        Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");
        Requires.NotNull(metadata);
        Requires.Argument(classType == metadata.ClassType, nameof(metadata), "Metadata must describe the target class.");

        if (NonPublicClass.TryGetValue(classType, out RpcTargetMetadata? result))
        {
            return result;
        }

        Builder builder = new(metadata);
        AddMethods(builder, classType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
        AddEvents(builder, classType.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        result = builder.ToImmutable();

        // If the caller does not have a complete idea of all interfaces that the class implements,
        // we can still return the result, but we will not cache it since that may pollute other users with
        // more complete inputs.
        IReadOnlyList<Type> missingInterfaces = metadata.GetMissingInterfacesFromSet();
        if (missingInterfaces is not [])
        {
            return result;
        }

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

    private static IReadOnlyList<Type> GetMissingInterfacesFromSet([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type targetType, IReadOnlyList<RpcTargetInterface> interfaces, int startIndex)
    {
        List<Type>? missing = null;

        // Verify that all interfaces are present.
        foreach (Type derivedFrom in targetType.GetInterfaces())
        {
            bool found = false;
            for (int i = startIndex; i < interfaces.Count; i++)
            {
                if (interfaces[i].Interface == derivedFrom)
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

    internal struct RpcTargetInterface([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)]
        public Type Interface => iface;
    }

    /// <summary>
    /// Represents a collection of interfaces implemented by a specified class type for use as an RPC target.
    /// </summary>
    /// <remarks>
    /// Use this class to track and manage the interfaces that a given class type implements,
    /// typically for remote procedure call (RPC) scenarios. Interfaces must be added explicitly after construction
    /// using the Add method, unless the Create factory method is used to automatically populate the collection.
    /// </remarks>
    public class ClassAndInterfaces
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
        private readonly Type classType;

        private readonly List<RpcTargetInterface> interfaces = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="ClassAndInterfaces"/> class.
        /// </summary>
        /// <param name="classType">The class type serving as the RPC target.</param>
        /// <remarks>
        /// <para>
        /// After construction, all interfaces that the <paramref name="classType"/> implements
        /// must be added using the <see cref="Add(Type)"/> method.
        /// </para>
        /// <para>
        /// Use the <see cref="Create(Type)"/> factory method to automate full initialization of this collection,
        /// at the cost of a less trimmable application.
        /// </para>
        /// </remarks>
        public ClassAndInterfaces([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type classType)
        {
            Requires.NotNull(classType);
            Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");

            this.classType = classType;
        }

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
        internal Type ClassType => this.classType;

        internal int InterfaceCount => this.interfaces.Count;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        internal Type this[int index] => this.interfaces[index].Interface;

        /// <summary>
        /// Creates a new instance of the <see cref="ClassAndInterfaces"/> class that represents the specified class type and all of
        /// its implemented interfaces.
        /// </summary>
        /// <param name="classType">The Type object representing the class to include, along with all interfaces implemented by the class. Must
        /// not be null.</param>
        /// <returns>A <see cref="ClassAndInterfaces"/> instance containing the specified class type and all interfaces it implements.</returns>
        [SuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "We use the All link demand on rpcContract, so results of GetInterfaces() should work. See https://github.com/dotnet/linker/issues/1731")]
        [UnconditionalSuppressMessage("Trimming", "IL2062", Justification = "We use the All link demand on rpcContract, so results of GetInterfaces() should work. See https://github.com/dotnet/linker/issues/1731")]
        public static ClassAndInterfaces Create([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type classType)
        {
            ClassAndInterfaces result = new(classType);
            foreach (Type iface in classType.GetInterfaces())
            {
                result.Add(iface);
            }

            return result;
        }

        /// <summary>
        /// Adds an interface to the set of interfaces supported by the RPC target.
        /// </summary>
        /// <param name="iface">The interface type to add. Must be an interface implemented by the target class.</param>
        public void Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
        {
            Requires.NotNull(iface);
            Requires.Argument(iface.IsInterface && iface.IsAssignableFrom(this.classType), nameof(iface), "This type must be an interface that the class implements.");

            this.interfaces.Add(new RpcTargetInterface(iface));
        }

        internal IReadOnlyList<Type> GetMissingInterfacesFromSet() => RpcTargetMetadata.GetMissingInterfacesFromSet(this.classType, this.interfaces, 0);
    }

    /// <summary>
    /// Represents a collection of interface types associated with a primary interface for an RPC target.
    /// Provides enumeration and management of the primary interface and its base interfaces.
    /// </summary>
    /// <remarks>
    /// This class is typically used to track and expose the set of interfaces implemented by an RPC target,
    /// ensuring that all relevant contract interfaces are available for reflection or invocation scenarios.
    /// </remarks>
    public class InterfaceCollection : IEnumerable<Type>
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)]
        private Type primaryInterface;

        private List<RpcTargetInterface> interfaces;

        /// <summary>
        /// Initializes a new instance of the <see cref="InterfaceCollection"/> class.
        /// </summary>
        /// <param name="primaryInterface">The primary RPC target interface.</param>
        /// <remarks>
        /// <para>
        /// After construction, all interfaces that the <paramref name="primaryInterface"/> derives from
        /// must be added using the <see cref="Add(Type)"/> method.
        /// </para>
        /// <para>
        /// Use the <see cref="Create(Type)"/> factory method to automate full initialization of this collection,
        /// at the cost of a less trimmable application.
        /// </para>
        /// </remarks>
        public InterfaceCollection([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)] Type primaryInterface)
        {
            Requires.NotNull(primaryInterface);
            Requires.Argument(primaryInterface.IsInterface, nameof(primaryInterface), "The type must be an interface.");

            this.primaryInterface = primaryInterface;
            this.interfaces = [new(primaryInterface)];
        }

        /// <summary>
        /// Gets the number of interfaces in the collection, including the primary interface.
        /// </summary>
        internal int Count => this.interfaces.Count;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.Interfaces)]
        internal Type PrimaryInterface => this.primaryInterface;

        /// <summary>
        /// Gets the interface type at the specified index in the collection.
        /// </summary>
        /// <param name="index">The zero-based index of the interface to retrieve. The zero-index interface is always the <see cref="PrimaryInterface"/>.</param>
        /// <returns>The <see cref="Type"/> representing the interface at the specified index.</returns>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)]
        internal Type this[int index] => this.interfaces[index].Interface;

        /// <summary>
        /// Adds an interface to the set of base interfaces for this RPC target.
        /// </summary>
        /// <param name="iface">The interface type to add. Must be an interface from which the primary interface derives. Cannot be null.</param>
        public void Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type iface)
        {
            Requires.NotNull(iface);
            Requires.Argument(iface.IsInterface && iface.IsAssignableFrom(this.interfaces[0].Interface), nameof(iface), "This type must be an interface from which the primary interface derives.");

            this.interfaces.Add(new RpcTargetInterface(iface));
        }

        /// <inheritdoc/>
        public IEnumerator<Type> GetEnumerator() => this.interfaces.Select(t => t.Interface).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        [SuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "We use the All link demand on rpcContract, so results of GetInterfaces() should work. See https://github.com/dotnet/linker/issues/1731")]
        [UnconditionalSuppressMessage("Trimming", "IL2062", Justification = "We use the All link demand on rpcContract, so results of GetInterfaces() should work. See https://github.com/dotnet/linker/issues/1731")]
        internal static InterfaceCollection Create([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type primaryInterface)
        {
            InterfaceCollection result = new(primaryInterface);
            foreach (Type iface in primaryInterface.GetInterfaces())
            {
                result.Add(iface);
            }

            return result;
        }

        internal IReadOnlyList<Type> GetMissingInterfacesFromSet() => RpcTargetMetadata.GetMissingInterfacesFromSet(this.primaryInterface, this.interfaces, 1);
    }

    /// <summary>
    /// Provides metadata describing an RPC event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this class to access information about an event and its handler in scenarios where events
    /// are invoked via RPC mechanisms. The metadata includes the event's reflection information, the name of the RPC
    /// method to invoke, the expected delegate type for the handler, and a factory for creating handler delegates. This
    /// class is typically used in frameworks or infrastructure that dynamically manage event subscriptions and
    /// invocations.
    /// </para>
    /// <para>
    /// Instances of this class are generally constructed internally and are not intended to be created
    /// directly by consumers.
    /// </para>
    /// </remarks>
    public class EventMetadata
    {
        /// <summary>
        /// Gets the event for which this metadata is describing the handler.
        /// </summary>
        public required EventInfo Event { get; init; }

        /// <summary>
        /// Gets the name of the RPC method that this event will invoke when raised.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the delegate type of the event handler.
        /// </summary>
        public required Type EventHandlerType { get; init; }

        /// <summary>
        /// Gets a factory method that creates a delegate to handle the event.
        /// </summary>
        public required CreateEventHandlerDelegate CreateEventHandler { get; init; }
    }

    /// <summary>
    /// Represents metadata about an RPC target method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is typically used to encapsulate information about a method that can be invoked
    /// via JSON-RPC. It provides access to the method's reflection data and any custom attributes relevant to JSON-RPC
    /// dispatch.
    /// </para>
    /// <para>
    /// Instances of this class are generally constructed internally and are not intended to be created
    /// directly by consumers.
    /// </para>
    /// </remarks>
    [DebuggerDisplay($"{{{nameof(DebuggerDisplay)},nq}}")]
    public class TargetMethodMetadata
    {
        private ParameterInfo[]? parameters;

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> for the RPC target method.
        /// </summary>
        public required MethodInfo Method { get; init; }

        /// <summary>
        /// Gets the RPC target name that should invoke this method.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the <see cref="JsonRpcMethodAttribute"/> that applies to this method, if any.
        /// </summary>
        public required JsonRpcMethodAttribute? Attribute { get; init; }

        /// <summary>
        /// Gets the parameters on the method.
        /// </summary>
        /// <remarks>
        /// This is equivalent to <see cref="MethodBase.GetParameters"/>, but cached for performance.
        /// </remarks>
        internal IReadOnlyList<ParameterInfo> Parameters => this.parameters ??= this.Method.GetParameters() ?? [];

        /// <summary>
        /// Gets a <see cref="ReadOnlyMemory{T}"/> view of the parameters on the method.
        /// </summary>
        /// <seealso cref="Parameters"/>
        internal ReadOnlyMemory<ParameterInfo> ParametersMemory => (ParameterInfo[])this.Parameters;

        /// <summary>
        /// Gets a value indicating whether the method is declared as public.
        /// </summary>
        internal bool IsPublic => this.Method.IsPublic;

        internal int RequiredParamCount => this.Parameters.Count(pi => !pi.IsOptional && pi.ParameterType != typeof(CancellationToken));

        internal int TotalParamCountExcludingCancellationToken => this.HasCancellationTokenParameter ? this.Parameters.Count - 1 : this.Parameters.Count;

        internal bool HasCancellationTokenParameter => this.Parameters is [.., { ParameterType: { } type }] && type == typeof(CancellationToken);

        internal bool HasOutOrRefParameters => this.Parameters.Any(pi => pi.IsOut || pi.ParameterType.IsByRef);

        [ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{this.Method.DeclaringType}.{this.Name}({string.Join(", ", this.Parameters.Select(p => p.ParameterType.Name))})";

        /// <inheritdoc/>
        public override string ToString() => this.DebuggerDisplay;

        internal static TargetMethodMetadata From(MethodInfo method, JsonRpcMethodAttribute? attribute)
            => new()
            {
                Method = method,
                Name = attribute?.Name ?? method.Name,
                Attribute = attribute,
            };

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

    private class Builder
    {
        internal Builder(InterfaceCollection interfaces)
        {
            this.TargetType = interfaces.PrimaryInterface;
        }

        internal Builder(ClassAndInterfaces classAndInterfaces)
        {
            this.TargetType = classAndInterfaces.ClassType;

            InterfaceMapping[] mapping = new InterfaceMapping[classAndInterfaces.InterfaceCount];
            for (int i = 0; i < classAndInterfaces.InterfaceCount; i++)
            {
                mapping[i] = this.TargetType.GetTypeInfo().GetInterfaceMap(classAndInterfaces[i]);
            }

            this.InterfaceMaps = mapping;
        }

        internal ReadOnlyMemory<InterfaceMapping> InterfaceMaps { get; }

        internal Type TargetType { get; }

        internal Dictionary<string, List<TargetMethodMetadata>> Methods { get; } = new(StringComparer.Ordinal);

        internal List<EventMetadata> Events { get; } = [];

        internal RpcTargetMetadata ToImmutable()
        {
            this.GenerateAliases();

            return new RpcTargetMetadata
            {
                TargetType = this.TargetType,
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
