// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using StreamJsonRpc.Protocol;

namespace StreamJsonRpc.Reflection;

internal class RpcTargetInfo : System.IAsyncDisposable
{
    private readonly JsonRpc jsonRpc;

    /// <summary>
    /// A collection of target objects and their map of clr method to <see cref="JsonRpcMethodAttribute"/> values.
    /// </summary>
    /// <remarks>
    /// Access to this collection should be guarded by <see cref="SyncObject"/>.
    /// </remarks>
    private readonly Dictionary<string, List<MethodSignatureAndTarget>> targetRequestMethodToClrMethodMap = new(StringComparer.Ordinal);

    /// <summary>
    /// A list of event handlers we've registered on target objects that define events. May be <see langword="null"/> if there are no handlers.
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
        // Unregister event handlers first to prevent any events from being raised during disposal.
        this.UnregisterEventHandlersFromTargetObjects();

        List<object>? objectsToDispose;
        lock (this.SyncObject)
        {
            objectsToDispose = this.localTargetObjectsToDispose;
            this.localTargetObjectsToDispose = null;
        }

        if (objectsToDispose is object)
        {
            List<Exception>? exceptions = null;
            foreach (object target in objectsToDispose)
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
                catch (Exception ex)
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
                        return entry.Attribute;
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
            if (this.targetRequestMethodToClrMethodMap.TryGetValue(request.Method, out List<MethodSignatureAndTarget>? candidateTargets))
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
    /// <param name="targetType">The description of the RPC target.</param>
    /// <param name="target">Target to invoke when incoming messages are received.</param>
    /// <param name="options">A set of customizations for how the target object is registered. If <see langword="null"/>, default options will be used.</param>
    /// <param name="revertAddLocalRpcTarget">An optional object that may be disposed of to revert the addition of the target object.</param>
    /// <remarks>
    /// When multiple target objects are added, the first target with a method that matches a request is invoked.
    /// </remarks>
    internal void AddLocalRpcTarget(
        RpcTargetMetadata targetType,
        object target,
        JsonRpcTargetOptions options,
        RevertAddLocalRpcTarget? revertAddLocalRpcTarget)
    {
        Requires.Argument(targetType.TargetType.IsAssignableFrom(target.GetType()), nameof(target), "Target object must be assignable to the target type.");

        lock (this.SyncObject)
        {
            this.AddRpcInterfaceToTarget(targetType, target, options, revertAddLocalRpcTarget);

            if (options.NotifyClientOfEvents)
            {
                foreach (RpcTargetMetadata.EventMetadata evt in targetType.Events)
                {
                    this.eventReceivers ??= [];

                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
                    {
                        this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)JsonRpc.TraceEvents.LocalEventListenerAdded, "Listening for events from {0}.{1} to raise notification.", target.GetType().FullName, evt.Name);
                    }

                    var eventReceiver = new EventReceiver(this.jsonRpc, target, evt, options);
                    revertAddLocalRpcTarget?.RecordEventReceiver(eventReceiver);
                    this.eventReceivers.Add(eventReceiver);
                }
            }

            if (options.DisposeOnDisconnect)
            {
                if (this.localTargetObjectsToDispose is null)
                {
                    this.localTargetObjectsToDispose = new List<object>();
                }

                revertAddLocalRpcTarget?.RecordObjectToDispose(target);
                this.localTargetObjectsToDispose.Add(target);
            }
        }
    }

    /// <summary>
    /// Adds the specified target as possible object to invoke when incoming messages are received.
    /// </summary>
    /// <param name="exposingMembersOn">The description of the RPC target.</param>
    /// <param name="target">Target to invoke when incoming messages are received.</param>
    /// <param name="options">A set of customizations for how the target object is registered. If <see langword="null"/>, default options will be used.</param>
    /// <param name="requestRevertOption"><see langword="true"/> to receive an <see cref="IDisposable"/> that can remove the target object; <see langword="false" /> otherwise.</param>
    /// <returns>An object that may be disposed of to revert the addition of the target object. Will be null if and only if <paramref name="requestRevertOption"/> is <see langword="false"/>.</returns>
    /// <remarks>
    /// When multiple target objects are added, the first target with a method that matches a request is invoked.
    /// </remarks>
    internal RevertAddLocalRpcTarget? AddLocalRpcTarget(
        RpcTargetMetadata exposingMembersOn,
        object target,
        JsonRpcTargetOptions options,
        bool requestRevertOption)
    {
        RevertAddLocalRpcTarget? revert = requestRevertOption ? new RevertAddLocalRpcTarget(this) : null;
        this.AddLocalRpcTarget(exposingMembersOn, target, options, revert);
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
    /// An attribute will *not* be discovered via reflection on the <paramref name="handler"/>, even if this value is <see langword="null"/>.
    /// </param>
    /// <param name="synchronizationContext">The <see cref="System.Threading.SynchronizationContext"/> to schedule the method invocation on instead of the default one specified by the <see cref="SynchronizationContext"/> property.</param>
    internal void AddLocalRpcMethod(MethodInfo handler, object? target, JsonRpcMethodAttribute? methodRpcSettings, SynchronizationContext? synchronizationContext)
    {
        Requires.NotNull(handler, nameof(handler));
        Requires.Argument(handler.IsStatic == (target is null), nameof(target), Resources.TargetObjectAndMethodStaticFlagMismatch);

        string rpcMethodName = methodRpcSettings?.Name ?? handler.Name;
        lock (this.SyncObject)
        {
            MethodSignatureAndTarget methodTarget = new(RpcTargetMetadata.TargetMethodMetadata.From(handler, methodRpcSettings, shape: null, methodShapeAttribute: null), target, attribute: null, synchronizationContext);
            this.TraceLocalMethodAdded(rpcMethodName, methodTarget);
            if (this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out List<MethodSignatureAndTarget>? existingList))
            {
                if (existingList.Any(m => m.Signature.EqualSignature(methodTarget.Signature)))
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
        if (this.eventReceivers is not null)
        {
            foreach (EventReceiver receiver in this.eventReceivers)
            {
                receiver.Dispose();
            }

            this.eventReceivers = null;
        }
    }

    /// <summary>
    /// Adds a new RPC interface to an existing target registering RPC methods.
    /// </summary>
    /// <param name="targetType">A description of the members on <paramref name="target"/> to be mapped in as RPC targets.</param>
    /// <param name="target">Target to invoke when incoming messages are received.</param>
    /// <param name="options">A set of customizations for how the target object is registered. If <see langword="null"/>, default options will be used.</param>
    /// <param name="revertAddLocalRpcTarget">An optional object that may be disposed of to revert the addition of the target object.</param>
    internal void AddRpcInterfaceToTarget(RpcTargetMetadata targetType, object target, JsonRpcTargetOptions options, RevertAddLocalRpcTarget? revertAddLocalRpcTarget)
    {
        JsonRpcMethodAttribute? pseudoAttribute = (options.ClientRequiresNamedArguments || options.UseSingleObjectParameterDeserialization)
            ? new() { ClientRequiresNamedArguments = options.ClientRequiresNamedArguments, UseSingleObjectParameterDeserialization = options.UseSingleObjectParameterDeserialization }
            : null;

        lock (this.SyncObject)
        {
            foreach (KeyValuePair<string, IReadOnlyList<RpcTargetMetadata.TargetMethodMetadata>> item in targetType.Methods.Concat(targetType.AliasedMethods))
            {
                string rpcMethodName = options.MethodNameTransform is not null ? options.MethodNameTransform(item.Key) : item.Key;
                Requires.Argument(rpcMethodName is not null, nameof(options), nameof(JsonRpcTargetOptions.MethodNameTransform) + " delegate returned a value that is not a legal RPC method name.");
                bool alreadyExists = this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out List<MethodSignatureAndTarget>? existingList);
                if (!alreadyExists)
                {
                    this.targetRequestMethodToClrMethodMap.Add(rpcMethodName, existingList = new List<MethodSignatureAndTarget>());
                }

                // Only add methods that do not have equivalent signatures to what we already have.
                foreach (RpcTargetMetadata.TargetMethodMetadata newMethod in item.Value)
                {
                    // Null forgiveness operator in use due to: https://github.com/dotnet/roslyn/issues/73274
                    if (!alreadyExists || !existingList!.Any(e => e.Equals(newMethod)))
                    {
                        var signatureAndTarget = new MethodSignatureAndTarget(newMethod, target, pseudoAttribute, null);
                        this.TraceLocalMethodAdded(rpcMethodName, signatureAndTarget);
                        revertAddLocalRpcTarget?.RecordMethodAdded(rpcMethodName, signatureAndTarget);
                        existingList!.Add(signatureAndTarget);
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
        }
    }

    private void TraceLocalMethodAdded(string rpcMethodName, MethodSignatureAndTarget targetMethod)
    {
        Requires.NotNullOrEmpty(rpcMethodName, nameof(rpcMethodName));

        if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
        {
            this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)JsonRpc.TraceEvents.LocalMethodAdded, "Added local RPC method \"{0}\" -> {1}", rpcMethodName, targetMethod);
        }
    }

    /// <summary>
    /// A class whose disposal will revert certain effects of a prior call to <see cref="AddLocalRpcTarget(RpcTargetMetadata, object, JsonRpcTargetOptions?, bool)"/>.
    /// </summary>
    internal class RevertAddLocalRpcTarget : IDisposable
    {
        private readonly RpcTargetInfo owner;
        private object? objectToDispose;
        private List<(string RpcMethodName, MethodSignatureAndTarget Method)>? targetMethods;
        private List<IDisposable>? eventReceivers;

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

        internal void RecordEventReceiver(IDisposable eventReceiver)
        {
            if (this.eventReceivers is null)
            {
                this.eventReceivers = new List<IDisposable>();
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
        private readonly JsonRpc jsonRpc;
        private readonly object server;
        private readonly Action<object?, Delegate> removeEventHandler;
        private readonly Delegate registeredHandler;
        private readonly string rpcEventName;

        internal EventReceiver(JsonRpc jsonRpc, object server, RpcTargetMetadata.EventMetadata eventMetadata, JsonRpcTargetOptions options)
        {
            Requires.NotNull(jsonRpc);
            Requires.NotNull(server);
            Requires.NotNull(eventMetadata);

            options = options ?? JsonRpcTargetOptions.Default;

            this.jsonRpc = jsonRpc;
            this.server = server;
            this.removeEventHandler = eventMetadata.RemoveEventHandler;

            this.rpcEventName = options.EventNameTransform is not null ? options.EventNameTransform(eventMetadata.Name) : eventMetadata.Name;

            this.registeredHandler = eventMetadata.CreateEventHandler(jsonRpc, this.rpcEventName);
            eventMetadata.AddEventHandler(server, this.registeredHandler);
        }

        public void Dispose()
        {
            this.removeEventHandler(this.server, this.registeredHandler);
        }
    }
}
