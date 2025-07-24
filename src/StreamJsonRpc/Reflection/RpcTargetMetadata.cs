// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.VisualStudio.Threading;

namespace StreamJsonRpc.Reflection;

public class RpcTargetMetadata
{
    private static readonly ConcurrentDictionary<Type, IEventHandlerFactory> EventHandlerFactories = [];

    public delegate Delegate CreateEventHandlerDelegate(JsonRpc rpc, string eventName);

    public interface IEventHandlerFactory
    {
        /// <summary>
        /// Creates an event handler for the specified event.
        /// </summary>
        /// <param name="rpc">The JSON-RPC instance to use for sending notifications.</param>
        /// <param name="eventName">The name of the event to create a handler for.</param>
        /// <returns>A delegate that can be used as an event handler.</returns>
        Delegate CreateEventHandler(JsonRpc rpc, string eventName);
    }

    public required IReadOnlyDictionary<string, ReadOnlyMemory<TargetMethodMetadata>> Methods { get; init; }

    public required IReadOnlyList<EventMetadata> Events { get; init; }

    public static RpcTargetMetadata FromClass([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicEvents)] Type classType)
    {
        Requires.NotNull(classType);
        Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");

        Builder builder = new();
        AddMethods(builder, classType.GetMethods(BindingFlags.Public | BindingFlags.Instance));
        AddEvents(builder, classType.GetEvents(BindingFlags.Public | BindingFlags.Instance));

        return builder.ToImmutable();
    }

    public static RpcTargetMetadata FromClassNonPublic([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicEvents)] Type classType)
    {
        Requires.NotNull(classType);
        Requires.Argument(classType.IsClass, nameof(classType), "The type must be a class.");

        Builder builder = new();
        AddMethods(builder, classType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        AddEvents(builder, classType.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        return builder.ToImmutable();
    }

    public static void RegisterEventHandlerFactory<T>()
        where T : struct => EventHandlerFactories.TryAdd(typeof(T), new EventHandlerFactory<T>());

    private static void AddMethods(Builder builder, IEnumerable<MethodInfo> methods)
    {
        foreach (MethodInfo method in methods)
        {
            TryAddCandidateMethod(builder, method);
        }
    }

    private static bool TryAddCandidateMethod(Builder builder, MethodInfo method)
    {
        if (method.IsSpecialName || method.IsConstructor || method.IsStatic || method.DeclaringType == typeof(object))
        {
            return false;
        }

        if (method.GetCustomAttribute<JsonRpcIgnoreAttribute>() is not null)
        {
            return false;
        }

        JsonRpcMethodAttribute? methodAttribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
        string name = methodAttribute?.Name ?? method.Name;

        if (!builder.Methods.TryGetValue(name, out List<TargetMethodMetadata>? methodList))
        {
            builder.Methods[name] = methodList = [];
        }

        methodList.Add(TargetMethodMetadata.From(method, methodAttribute));
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

        CreateEventHandlerDelegate createEventHandler;
        if (@event.EventHandlerType == typeof(EventHandler))
        {
            createEventHandler = (rpc, eventName) => new EventHandler((object? sender, EventArgs args) => rpc.NotifyAsync(eventName, [args]).Forget());
        }
        else if (@event.EventHandlerType.IsGenericType && @event.EventHandlerType.GetGenericTypeDefinition() == typeof(EventHandler<>) &&
            @event.EventHandlerType.GetGenericArguments() is [{ } argType])
        {
            if (!argType.IsValueType)
            {
                Type[] argTypes = [argType];
                createEventHandler = (rpc, eventName) => (object? sender, object? args) => rpc.NotifyAsync(eventName, new object?[] { args }, argType).Forget();
            }
            else if (EventHandlerFactories.TryGetValue(argType, out IEventHandlerFactory? factory))
            {
                createEventHandler = (rpc, eventName) => factory.CreateEventHandler(rpc, eventName);
            }
            else
            {
                // We don't have a factory registered for this value type.
                return false;
            }
        }
        else
        {
            // We don't support this delegate type.
            return false;
        }

        builder.Events.Add(new EventMetadata
        {
            Event = @event,
            EventHandlerType = @event.EventHandlerType,
            CreateEventHandler = createEventHandler,
        });
        return true;
    }

    public struct EventMetadata
    {
        public required EventInfo Event { get; init; }

        public required Type EventHandlerType { get; init; }

        public required CreateEventHandlerDelegate CreateEventHandler { get; init; }
    }

    public struct TargetMethodMetadata
    {
        public required MethodInfo Method { get; init; }

        /// <summary>
        /// Gets a value indicating whether JSON-RPC named arguments should all be deserialized into the RPC method's first parameter.
        /// </summary>
        public bool UseSingleObjectParameterDeserialization { get; init; }

        /// <summary>
        /// Gets a value indicating whether JSON-RPC named arguments should be used in callbacks sent back to the client.
        /// </summary>
        /// <value>The default value is <see langword="false"/>.</value>
        /// <remarks>
        /// An example of impact of this setting is when the client sends an <see cref="IProgress{T}"/> argument and this server
        /// will call <see cref="IProgress{T}.Report(T)"/> on that argument.
        /// The notification that the server then sends back to the client may use positional or named arguments in that notification.
        /// Named arguments are used if and only if this property is set to <see langword="true" />.
        /// </remarks>
        public bool ClientRequiresNamedArguments { get; init; }

        internal static TargetMethodMetadata From(MethodInfo method, JsonRpcMethodAttribute? attribute)
            => new()
            {
                Method = method,
                UseSingleObjectParameterDeserialization = attribute?.UseSingleObjectParameterDeserialization ?? false,
                ClientRequiresNamedArguments = attribute?.UseSingleObjectParameterDeserialization ?? false,
            };
    }

    private class EventHandlerFactory<T> : IEventHandlerFactory
    {
        public Delegate CreateEventHandler(JsonRpc rpc, string eventName)
        {
            Type[] argTypes = [typeof(T)];
            return (object? sender, T args) => rpc.NotifyAsync(eventName, [args], argTypes).Forget();
        }
    }

    private class Builder
    {
        internal Dictionary<string, List<TargetMethodMetadata>> Methods { get; } = new(StringComparer.Ordinal);

        internal List<EventMetadata> Events { get; } = [];

        internal RpcTargetMetadata ToImmutable()
            => new RpcTargetMetadata
            {
                Methods = this.Methods.ToImmutableDictionary(kv => kv.Key, kv => (ReadOnlyMemory<TargetMethodMetadata>)kv.Value.ToArray()),
                Events = this.Events.ToImmutableArray(),
            };
    }
}
