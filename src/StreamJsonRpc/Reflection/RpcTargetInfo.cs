// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using StreamJsonRpc.Protocol;

    internal class RpcTargetInfo : System.IAsyncDisposable
    {
        private const string ImpliedMethodNameAsyncSuffix = "Async";
        private static readonly Dictionary<TypeInfo, MethodNameMap> MethodNameMaps = new Dictionary<TypeInfo, MethodNameMap>();
        private static readonly Dictionary<(TypeInfo Type, bool AllowNonPublicInvocation, bool UseSingleObjectParameterDeserialization), Dictionary<string, List<MethodSignature>>> RequestMethodToClrMethodMap = new Dictionary<(TypeInfo Type, bool AllowNonPublicInvocation, bool UseSingleObjectParameterDeserialization), Dictionary<string, List<MethodSignature>>>();
        private readonly JsonRpc jsonRpc;

        /// <summary>
        /// A collection of target objects and their map of clr method to <see cref="JsonRpcMethodAttribute"/> values.
        /// </summary>
        private readonly Dictionary<string, List<MethodSignatureAndTarget>> targetRequestMethodToClrMethodMap = new Dictionary<string, List<MethodSignatureAndTarget>>(StringComparer.Ordinal);

        /// <summary>
        /// A list of event handlers we've registered on target objects that define events. May be <c>null</c> if there are no handlers.
        /// </summary>
        private List<EventReceiver>? eventReceivers;

        /// <summary>
        /// A lazily-initialized list of objects to dispose of when the JSON-RPC connection drops.
        /// </summary>
        private List<object>? localTargetObjectsToDispose;

        internal RpcTargetInfo(JsonRpc jsonRpc)
        {
            this.jsonRpc = jsonRpc;
        }

        private TraceSource TraceSource => this.jsonRpc.TraceSource;

        private object SyncObject => this.targetRequestMethodToClrMethodMap;

        public async ValueTask DisposeAsync()
        {
            if (this.localTargetObjectsToDispose is object)
            {
                List<Exception>? exceptions = null;
                foreach (object target in this.localTargetObjectsToDispose)
                {
                    // We're calling Dispose on the target objects, so switch to the user-supplied SyncContext for those target objects.
                    await this.jsonRpc.SynchronizationContextOrDefault;

                    try
                    {
                        // Arrange to dispose of the target when the connection is closed.
                        if (target is System.IAsyncDisposable asyncDisposableTarget)
                        {
                            await asyncDisposableTarget.DisposeAsync().ConfigureAwait(false);
                        }
                        else if (target is Microsoft.VisualStudio.Threading.IAsyncDisposable vsAsyncDisposableTarget)
                        {
                            await vsAsyncDisposableTarget.DisposeAsync().ConfigureAwait(false);
                        }
                        else if (target is IDisposable disposableTarget)
                        {
                            disposableTarget.Dispose();
                        }
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        exceptions ??= new List<Exception>();
                        exceptions.Add(ex);
                    }
                }

                if (exceptions is object)
                {
                    if (exceptions.Count == 1)
                    {
                        ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
                    }

                    throw new AggregateException(exceptions);
                }
            }
        }

        internal static MethodNameMap GetMethodNameMap(TypeInfo type)
        {
            MethodNameMap? map;
            lock (MethodNameMaps)
            {
                if (MethodNameMaps.TryGetValue(type, out map))
                {
                    return map;
                }
            }

            map = new MethodNameMap(type);

            lock (MethodNameMaps)
            {
                if (MethodNameMaps.TryGetValue(type, out MethodNameMap? lostRaceMap))
                {
                    return lostRaceMap;
                }

                MethodNameMaps.Add(type, map);
            }

            return map;
        }

        /// <summary>
        /// Gets the <see cref="JsonRpcMethodAttribute"/> for a previously discovered RPC method, if there is one.
        /// </summary>
        /// <param name="methodName">The name of the method for which the attribute is sought.</param>
        /// <param name="parameters">
        /// The list of parameters found on the method, as they may be given to <see cref="JsonRpcRequest.TryGetTypedArguments(ReadOnlySpan{ParameterInfo}, Span{object})"/>.
        /// Note this list may omit some special parameters such as a trailing <see cref="CancellationToken"/>.
        /// </param>
        internal JsonRpcMethodAttribute? GetJsonRpcMethodAttribute(string methodName, ReadOnlySpan<ParameterInfo> parameters)
        {
            lock (this.SyncObject)
            {
                if (this.targetRequestMethodToClrMethodMap.TryGetValue(methodName, out List<MethodSignatureAndTarget>? existingList))
                {
                    foreach (MethodSignatureAndTarget entry in existingList)
                    {
                        if (entry.Signature.MatchesParametersExcludingCancellationToken(parameters))
                        {
                            return entry.Signature.Attribute;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Searches for a method to dispatch an incoming request to.
        /// </summary>
        /// <param name="request">The incoming request.</param>
        /// <param name="targetMethod">Receives information about the search. Check <see cref="TargetMethod.IsFound"/> to see if an exact signature match was found.</param>
        /// <returns>A value indicating whether the requested method name matches any that we accept. Use <see cref="TargetMethod.IsFound"/> on <paramref name="targetMethod"/> to confirm that an exact signature match is found.</returns>
        internal bool TryGetTargetMethod(JsonRpcRequest request, [NotNullWhen(true)] out TargetMethod? targetMethod)
        {
            Requires.Argument(request.Method is object, nameof(request), nameof(JsonRpcRequest.Method) + " must be set.");

            lock (this.SyncObject)
            {
                if (this.targetRequestMethodToClrMethodMap.TryGetValue(request.Method, out List<MethodSignatureAndTarget> candidateTargets))
                {
                    targetMethod = new TargetMethod(request, candidateTargets, this.jsonRpc.SynchronizationContextOrDefault);
                    return true;
                }

                targetMethod = null;
                return false;
            }
        }

        /// <summary>
        /// Adds the specified target as possible object to invoke when incoming messages are received.
        /// </summary>
        /// <param name="exposingMembersOn">
        /// The type whose members define the RPC accessible members of the <paramref name="target"/> object.
        /// If this type is not an interface, only public members become invokable unless <see cref="JsonRpcTargetOptions.AllowNonPublicInvocation"/> is set to true on the <paramref name="options"/> argument.
        /// </param>
        /// <param name="target">Target to invoke when incoming messages are received.</param>
        /// <param name="options">A set of customizations for how the target object is registered. If <c>null</c>, default options will be used.</param>
        /// <param name="requestRevertOption"><see langword="true"/> to receive an <see cref="IDisposable"/> that can remove the target object; <see langword="false" /> otherwise.</param>
        /// <returns>An object that may be disposed of to revert the addition of the target object. Will be null if and only if <paramref name="requestRevertOption"/> is <c>false</c>.</returns>
        /// <remarks>
        /// When multiple target objects are added, the first target with a method that matches a request is invoked.
        /// </remarks>
        internal IDisposable? AddLocalRpcTarget(Type exposingMembersOn, object target, JsonRpcTargetOptions? options, bool requestRevertOption)
        {
            RevertAddLocalRpcTarget? revert = requestRevertOption ? new RevertAddLocalRpcTarget(this) : null;
            options = options ?? JsonRpcTargetOptions.Default;
            IReadOnlyDictionary<string, List<MethodSignature>> mapping = GetRequestMethodToClrMethodMap(exposingMembersOn.GetTypeInfo(), options.AllowNonPublicInvocation, options.UseSingleObjectParameterDeserialization);

            lock (this.SyncObject)
            {
                foreach (KeyValuePair<string, List<MethodSignature>> item in mapping)
                {
                    string rpcMethodName = options.MethodNameTransform != null ? options.MethodNameTransform(item.Key) : item.Key;
                    Requires.Argument(rpcMethodName != null, nameof(options), nameof(JsonRpcTargetOptions.MethodNameTransform) + " delegate returned a value that is not a legal RPC method name.");
                    bool alreadyExists = this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out List<MethodSignatureAndTarget>? existingList);
                    if (!alreadyExists)
                    {
                        this.targetRequestMethodToClrMethodMap.Add(rpcMethodName, existingList = new List<MethodSignatureAndTarget>());
                    }

                    // Only add methods that do not have equivalent signatures to what we already have.
                    foreach (MethodSignature newMethod in item.Value)
                    {
                        if (!alreadyExists || !existingList.Any(e => e.Equals(newMethod)))
                        {
                            var signatureAndTarget = new MethodSignatureAndTarget(newMethod, target, null);
                            this.TraceLocalMethodAdded(rpcMethodName, signatureAndTarget);
                            revert?.RecordMethodAdded(rpcMethodName, signatureAndTarget);
                            existingList.Add(signatureAndTarget);
                        }
                        else
                        {
                            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Information, (int)JsonRpc.TraceEvents.LocalMethodAdded, "Skipping local RPC method \"{0}\" -> {1} because a method with a colliding signature has already been added.", rpcMethodName, newMethod);
                            }
                        }
                    }
                }

                if (options.NotifyClientOfEvents)
                {
                    HashSet<string>? eventsDiscovered = null;
                    for (TypeInfo? t = exposingMembersOn.GetTypeInfo(); t != null && t != typeof(object).GetTypeInfo(); t = t.BaseType?.GetTypeInfo())
                    {
                        foreach (EventInfo evt in t.DeclaredEvents)
                        {
                            if (evt.AddMethod is object && (evt.AddMethod.IsPublic || exposingMembersOn.IsInterface) && !evt.AddMethod.IsStatic)
                            {
                                if (this.eventReceivers == null)
                                {
                                    this.eventReceivers = new List<EventReceiver>();
                                }

                                if (eventsDiscovered is null)
                                {
                                    eventsDiscovered = new HashSet<string>(StringComparer.Ordinal);
                                }

                                if (!eventsDiscovered.Add(evt.Name))
                                {
                                    // Do not add the same event again. It can appear multiple times in a type hierarchy.
                                    continue;
                                }

                                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                                {
                                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)JsonRpc.TraceEvents.LocalEventListenerAdded, "Listening for events from {0}.{1} to raise notification.", target.GetType().FullName, evt.Name);
                                }

                                var eventReceiver = new EventReceiver(this.jsonRpc, target, evt, options);
                                revert?.RecordEventReceiver(eventReceiver);
                                this.eventReceivers.Add(eventReceiver);
                            }
                        }
                    }
                }

                if (options.DisposeOnDisconnect)
                {
                    if (this.localTargetObjectsToDispose is null)
                    {
                        this.localTargetObjectsToDispose = new List<object>();
                    }

                    revert?.RecordObjectToDispose(target);
                    this.localTargetObjectsToDispose.Add(target);
                }
            }

            return revert;
        }

        /// <summary>
        /// Adds a handler for an RPC method with a given name.
        /// </summary>
        /// <param name="handler">
        /// The method or delegate to invoke when a matching RPC message arrives.
        /// This method may accept parameters from the incoming JSON-RPC message.
        /// </param>
        /// <param name="target">An instance of the type that defines <paramref name="handler"/> which should handle the invocation.</param>
        /// <param name="methodRpcSettings">
        /// A description for how this method should be treated.
        /// It need not be an attribute that was actually applied to <paramref name="handler"/>.
        /// An attribute will *not* be discovered via reflection on the <paramref name="handler"/>, even if this value is <c>null</c>.
        /// </param>
        /// <param name="synchronizationContext">The <see cref="System.Threading.SynchronizationContext"/> to schedule the method invocation on instead of the default one specified by the <see cref="SynchronizationContext"/> property.</param>
        internal void AddLocalRpcMethod(MethodInfo handler, object? target, JsonRpcMethodAttribute? methodRpcSettings, SynchronizationContext? synchronizationContext)
        {
            Requires.NotNull(handler, nameof(handler));
            Requires.Argument(handler.IsStatic == (target == null), nameof(target), Resources.TargetObjectAndMethodStaticFlagMismatch);

            string rpcMethodName = methodRpcSettings?.Name ?? handler.Name;
            lock (this.SyncObject)
            {
                var methodTarget = new MethodSignatureAndTarget(handler, target, methodRpcSettings, synchronizationContext);
                this.TraceLocalMethodAdded(rpcMethodName, methodTarget);
                if (this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out List<MethodSignatureAndTarget>? existingList))
                {
                    if (existingList.Any(m => m.Signature.Equals(methodTarget.Signature)))
                    {
                        throw new InvalidOperationException(Resources.ConflictMethodSignatureAlreadyRegistered);
                    }

                    existingList.Add(methodTarget);
                }
                else
                {
                    this.targetRequestMethodToClrMethodMap.Add(rpcMethodName, new List<MethodSignatureAndTarget> { methodTarget });
                }
            }
        }

        internal void UnregisterEventHandlersFromTargetObjects()
        {
            if (this.eventReceivers != null)
            {
                foreach (EventReceiver receiver in this.eventReceivers)
                {
                    receiver.Dispose();
                }

                this.eventReceivers = null;
            }
        }

        /// <summary>
        /// Gets a dictionary which maps a request method name to its clr method name via <see cref="JsonRpcMethodAttribute" /> value.
        /// </summary>
        /// <param name="exposedMembersOnType">Type to reflect over and analyze its methods.</param>
        /// <param name="allowNonPublicInvocation"><inheritdoc cref="JsonRpcTargetOptions.AllowNonPublicInvocation" path="/summary"/></param>
        /// <param name="useSingleObjectParameterDeserialization"><inheritdoc cref="JsonRpcTargetOptions.UseSingleObjectParameterDeserialization" path="/summary"/></param>
        /// <returns>Dictionary which maps a request method name to its clr method name.</returns>
        private static IReadOnlyDictionary<string, List<MethodSignature>> GetRequestMethodToClrMethodMap(TypeInfo exposedMembersOnType, bool allowNonPublicInvocation, bool useSingleObjectParameterDeserialization)
        {
            Requires.NotNull(exposedMembersOnType, nameof(exposedMembersOnType));

            (TypeInfo Type, bool AllowNonPublicInvocation, bool UseSingleObjectParameterDeserialization) key = (exposedMembersOnType, allowNonPublicInvocation, useSingleObjectParameterDeserialization);
            Dictionary<string, List<MethodSignature>>? requestMethodToDelegateMap;
            lock (RequestMethodToClrMethodMap)
            {
                if (RequestMethodToClrMethodMap.TryGetValue(key, out requestMethodToDelegateMap))
                {
                    return requestMethodToDelegateMap;
                }
            }

            requestMethodToDelegateMap = new Dictionary<string, List<MethodSignature>>(StringComparer.Ordinal);
            var clrMethodToRequestMethodMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestMethodToClrMethodNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var candidateAliases = new Dictionary<string, string>(StringComparer.Ordinal);

            MethodNameMap mapping = GetMethodNameMap(exposedMembersOnType);

            for (TypeInfo? t = exposedMembersOnType; t != null && t != typeof(object).GetTypeInfo(); t = t.BaseType?.GetTypeInfo())
            {
                // As we enumerate methods, skip accessor methods
                foreach (MethodInfo method in t.DeclaredMethods.Where(m => !m.IsSpecialName))
                {
                    if (!key.AllowNonPublicInvocation && !method.IsPublic && !exposedMembersOnType.IsInterface)
                    {
                        continue;
                    }

                    var requestName = mapping.GetRpcMethodName(method);

                    if (!requestMethodToDelegateMap.TryGetValue(requestName, out List<MethodSignature>? methodList))
                    {
                        methodList = new List<MethodSignature>();
                        requestMethodToDelegateMap.Add(requestName, methodList);
                    }

                    // Verify that all overloads of this CLR method also claim the same request method name.
                    if (clrMethodToRequestMethodMap.TryGetValue(method.Name, out string? previousRequestNameUse))
                    {
                        if (!string.Equals(previousRequestNameUse, requestName, StringComparison.Ordinal))
                        {
                            Requires.Fail(Resources.ConflictingMethodNameAttribute, method.Name, nameof(JsonRpcMethodAttribute), nameof(JsonRpcMethodAttribute.Name));
                        }
                    }
                    else
                    {
                        clrMethodToRequestMethodMap.Add(method.Name, requestName);
                    }

                    // Verify that all CLR methods that want to use this request method name are overloads of each other.
                    if (requestMethodToClrMethodNameMap.TryGetValue(requestName, out string? previousClrNameUse))
                    {
                        if (!string.Equals(method.Name, previousClrNameUse, StringComparison.Ordinal))
                        {
                            Requires.Fail(Resources.ConflictingMethodAttributeValue, method.Name, previousClrNameUse, requestName);
                        }
                    }
                    else
                    {
                        requestMethodToClrMethodNameMap.Add(requestName, method.Name);
                    }

                    JsonRpcMethodAttribute? attribute = mapping.FindAttribute(method);

                    if (attribute == null && key.UseSingleObjectParameterDeserialization)
                    {
                        attribute = new JsonRpcMethodAttribute(null) { UseSingleObjectParameterDeserialization = true };
                    }

                    // Skip this method if its signature matches one from a derived type we have already scanned.
                    MethodSignature methodTarget = new MethodSignature(method, attribute);
                    if (methodList.Contains(methodTarget))
                    {
                        continue;
                    }

                    methodList.Add(methodTarget);

                    // If no explicit attribute has been applied, and the method ends with Async,
                    // register a request method name that does not include Async as well.
                    if (attribute?.Name == null && method.Name.EndsWith(ImpliedMethodNameAsyncSuffix, StringComparison.Ordinal))
                    {
                        string nonAsyncMethodName = method.Name.Substring(0, method.Name.Length - ImpliedMethodNameAsyncSuffix.Length);
                        if (!candidateAliases.ContainsKey(nonAsyncMethodName))
                        {
                            candidateAliases.Add(nonAsyncMethodName, method.Name);
                        }
                    }
                }
            }

            // Now that all methods have been discovered, add the candidate aliases
            // if it would not introduce any collisions.
            foreach (KeyValuePair<string, string> candidateAlias in candidateAliases)
            {
                if (!requestMethodToClrMethodNameMap.ContainsKey(candidateAlias.Key))
                {
                    requestMethodToClrMethodNameMap.Add(candidateAlias.Key, candidateAlias.Value);
                    requestMethodToDelegateMap[candidateAlias.Key] = requestMethodToDelegateMap[candidateAlias.Value].ToList();
                }
            }

            lock (RequestMethodToClrMethodMap)
            {
                if (RequestMethodToClrMethodMap.TryGetValue(key, out Dictionary<string, List<MethodSignature>>? lostRace))
                {
                    return lostRace;
                }

                RequestMethodToClrMethodMap.Add(key, requestMethodToDelegateMap);
            }

            return requestMethodToDelegateMap;
        }

        private void TraceLocalMethodAdded(string rpcMethodName, MethodSignatureAndTarget targetMethod)
        {
            Requires.NotNullOrEmpty(rpcMethodName, nameof(rpcMethodName));

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceEvent(TraceEventType.Information, (int)JsonRpc.TraceEvents.LocalMethodAdded, "Added local RPC method \"{0}\" -> {1}", rpcMethodName, targetMethod);
            }
        }

        internal class MethodNameMap
        {
            private readonly ReadOnlyMemory<InterfaceMapping> interfaceMaps;
            private readonly Dictionary<MethodInfo, JsonRpcMethodAttribute?> methodAttributes = new Dictionary<MethodInfo, JsonRpcMethodAttribute?>();

            internal MethodNameMap(TypeInfo typeInfo)
            {
                Requires.NotNull(typeInfo, nameof(typeInfo));
                this.interfaceMaps = typeInfo.IsInterface ? default
                    : typeInfo.ImplementedInterfaces.Select(typeInfo.GetInterfaceMap).ToArray();
            }

            internal string GetRpcMethodName(MethodInfo method)
            {
                Requires.NotNull(method, nameof(method));

                return this.FindAttribute(method)?.Name ?? method.Name;
            }

            /// <summary>
            /// Get the custom attribute, which may appear on the method itself or the interface definition of the method where applicable.
            /// </summary>
            /// <param name="method">The method to search for the attribute.</param>
            /// <returns>The attribute, if found.</returns>
            internal JsonRpcMethodAttribute? FindAttribute(MethodInfo method)
            {
                Requires.NotNull(method, nameof(method));

                JsonRpcMethodAttribute? attribute;
                lock (this.methodAttributes)
                {
                    if (this.methodAttributes.TryGetValue(method, out attribute))
                    {
                        return attribute;
                    }
                }

                attribute = (JsonRpcMethodAttribute?)method.GetCustomAttribute(typeof(JsonRpcMethodAttribute))
                    ?? (JsonRpcMethodAttribute?)this.FindMethodOnInterface(method)?.GetCustomAttribute(typeof(JsonRpcMethodAttribute));

                lock (this.methodAttributes)
                {
                    this.methodAttributes[method] = attribute;
                }

                return attribute;
            }

            private MethodInfo? FindMethodOnInterface(MethodInfo methodImpl)
            {
                Requires.NotNull(methodImpl, nameof(methodImpl));

                for (int i = 0; i < this.interfaceMaps.Length; i++)
                {
                    InterfaceMapping map = this.interfaceMaps.Span[i];
                    int methodIndex = Array.IndexOf(map.TargetMethods, methodImpl);
                    if (methodIndex >= 0)
                    {
                        return map.InterfaceMethods[methodIndex];
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// A class whose disposal will revert certain effects of a prior call to <see cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?, bool)"/>.
        /// </summary>
        private class RevertAddLocalRpcTarget : IDisposable
        {
            private readonly RpcTargetInfo owner;
            private object? objectToDispose;
            private List<(string RpcMethodName, MethodSignatureAndTarget Method)>? targetMethods;
            private List<EventReceiver>? eventReceivers;

            internal RevertAddLocalRpcTarget(RpcTargetInfo owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                lock (this.owner.SyncObject)
                {
                    if (this.objectToDispose is object)
                    {
                        this.owner.localTargetObjectsToDispose?.Remove(this.objectToDispose);
                    }

                    if (this.targetMethods is object)
                    {
                        foreach ((string RpcMethodName, MethodSignatureAndTarget Method) targetMethod in this.targetMethods)
                        {
                            if (this.owner.targetRequestMethodToClrMethodMap.TryGetValue(targetMethod.RpcMethodName, out List<MethodSignatureAndTarget>? list))
                            {
                                list.Remove(targetMethod.Method);
                            }
                        }
                    }

                    if (this.eventReceivers is object && this.owner.eventReceivers is object)
                    {
                        foreach (EventReceiver eventReceiver in this.eventReceivers)
                        {
                            this.owner.eventReceivers.Remove(eventReceiver);
                            eventReceiver.Dispose();
                        }
                    }

                    this.objectToDispose = null;
                    this.targetMethods = null;
                    this.eventReceivers = null;
                }
            }

            internal void RecordEventReceiver(EventReceiver eventReceiver)
            {
                if (this.eventReceivers is null)
                {
                    this.eventReceivers = new List<EventReceiver>();
                }

                this.eventReceivers.Add(eventReceiver);
            }

            internal void RecordMethodAdded(string rpcMethodName, MethodSignatureAndTarget newMethod)
            {
                if (this.targetMethods is null)
                {
                    this.targetMethods = new List<(string RpcMethodName, MethodSignatureAndTarget Method)>();
                }

                this.targetMethods.Add((rpcMethodName, newMethod));
            }

            internal void RecordObjectToDispose(object target)
            {
                Assumes.Null(this.objectToDispose);
                this.objectToDispose = target;
            }
        }

        private class EventReceiver : IDisposable
        {
            private static readonly MethodInfo OnEventRaisedMethodInfo = typeof(EventReceiver).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(OnEventRaised));
            private static readonly MethodInfo OnEventRaisedGenericMethodInfo = typeof(EventReceiver).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(OnEventRaisedGeneric));
            private readonly JsonRpc jsonRpc;
            private readonly object server;
            private readonly EventInfo eventInfo;
            private readonly Delegate registeredHandler;
            private readonly string rpcEventName;

            internal EventReceiver(JsonRpc jsonRpc, object server, EventInfo eventInfo, JsonRpcTargetOptions options)
            {
                Requires.NotNull(jsonRpc, nameof(jsonRpc));
                Requires.NotNull(server, nameof(server));
                Requires.NotNull(eventInfo, nameof(eventInfo));

                options = options ?? JsonRpcTargetOptions.Default;

                this.jsonRpc = jsonRpc;
                this.server = server;
                this.eventInfo = eventInfo;

                this.rpcEventName = options.EventNameTransform != null ? options.EventNameTransform(eventInfo.Name) : eventInfo.Name;

                try
                {
                    // This might throw if our EventHandler-modeled method doesn't "fit" the event delegate signature.
                    // It will work for EventHandler and EventHandler<T>, at least.
                    // If we want to support more, we'll likely have to use lightweight code-gen to generate a method
                    // with the right signature.
                    ParameterInfo[] eventHandlerParameters = eventInfo.EventHandlerType!.GetTypeInfo().GetMethod("Invoke")!.GetParameters();
                    if (eventHandlerParameters.Length != 2)
                    {
                        throw new NotSupportedException($"Unsupported event handler type for: \"{eventInfo.Name}\". Expected 2 parameters but had {eventHandlerParameters.Length}.");
                    }

                    Type argsType = eventHandlerParameters[1].ParameterType;
                    if (typeof(EventArgs).GetTypeInfo().IsAssignableFrom(argsType))
                    {
                        this.registeredHandler = OnEventRaisedMethodInfo.CreateDelegate(eventInfo.EventHandlerType!, this);
                    }
                    else
                    {
                        MethodInfo closedGenericMethod = OnEventRaisedGenericMethodInfo.MakeGenericMethod(argsType);
                        this.registeredHandler = closedGenericMethod.CreateDelegate(eventInfo.EventHandlerType!, this);
                    }
                }
                catch (ArgumentException ex)
                {
                    throw new NotSupportedException("Unsupported event handler type for: " + eventInfo.Name, ex);
                }

                eventInfo.AddEventHandler(server, this.registeredHandler);
            }

            public void Dispose()
            {
                this.eventInfo.RemoveEventHandler(this.server, this.registeredHandler);
            }

#pragma warning disable CA1801 // Review unused parameters
            private void OnEventRaisedGeneric<T>(object? sender, T args)
#pragma warning restore CA1801 // Review unused parameters
            {
                this.jsonRpc.NotifyAsync(this.rpcEventName, new object?[] { args }).Forget();
            }

            private void OnEventRaised(object? sender, EventArgs args)
            {
                this.jsonRpc.NotifyAsync(this.rpcEventName, new object[] { args }).Forget();
            }
        }
    }
}
