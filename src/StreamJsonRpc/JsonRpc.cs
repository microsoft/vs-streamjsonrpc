// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Manages a JSON-RPC connection with another entity over a <see cref="Stream"/>.
    /// </summary>
    public class JsonRpc : IDisposableObservable
    {
        private const string ImpliedMethodNameAsyncSuffix = "Async";
        private const string CancelRequestSpecialMethod = "$/cancelRequest";
        private static readonly ReadOnlyDictionary<string, string> EmptyDictionary = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        private static readonly object[] EmptyObjectArray = new object[0];
        private static readonly JsonSerializer DefaultJsonSerializer = JsonSerializer.CreateDefault();

        /// <summary>
        /// The <see cref="System.Threading.SynchronizationContext"/> to use to schedule work on the threadpool.
        /// </summary>
        private static readonly SynchronizationContext DefaultSynchronizationContext = new SynchronizationContext();

        private readonly object syncObject = new object();

        /// <summary>
        /// The object to lock when accessing the <see cref="resultDispatcherMap"/> or <see cref="inboundCancellationSources"/> objects.
        /// </summary>
        private readonly object dispatcherMapLock = new object();

        /// <summary>
        /// The object to lock when accessing the <see cref="DisconnectedPrivate"/> member.
        /// </summary>
        private readonly object disconnectedEventLock = new object();

        /// <summary>
        /// A map of outbound calls awaiting responses.
        /// Lock the <see cref="dispatcherMapLock"/> object for all access to this member.
        /// </summary>
        private readonly Dictionary<long, OutstandingCallData> resultDispatcherMap = new Dictionary<long, OutstandingCallData>();

        /// <summary>
        /// A map of id's from inbound calls that have not yet completed and may be canceled,
        /// to their <see cref="CancellationTokenSource"/> instances.
        /// Lock the <see cref="dispatcherMapLock"/> object for all access to this member.
        /// </summary>
        private readonly Dictionary<object, CancellationTokenSource> inboundCancellationSources = new Dictionary<object, CancellationTokenSource>();

        /// <summary>
        /// A delegate for the <see cref="CancelPendingOutboundRequest"/> method.
        /// </summary>
        private readonly Action<object> cancelPendingOutboundRequestAction;

        /// <summary>
        /// A delegate for the <see cref="HandleInvocationTaskResult(JsonRpcRequest, Task)"/> method.
        /// </summary>
        private readonly Func<Task, object, JsonRpcMessage> handleInvocationTaskResultDelegate;

        /// <summary>
        /// A collection of target objects and their map of clr method to <see cref="JsonRpcMethodAttribute"/> values.
        /// </summary>
        private readonly Dictionary<string, List<MethodSignatureAndTarget>> targetRequestMethodToClrMethodMap = new Dictionary<string, List<MethodSignatureAndTarget>>(StringComparer.Ordinal);

        /// <summary>
        /// The source for the <see cref="DisconnectedToken"/> property.
        /// </summary>
        private readonly CancellationTokenSource disconnectedSource = new CancellationTokenSource();

        /// <summary>
        /// The completion source behind <see cref="Completion"/>.
        /// </summary>
        private readonly TaskCompletionSource<bool> completionSource = new TaskCompletionSource<bool>();

        /// <summary>
        /// A list of event handlers we've registered on target objects that define events. May be <c>null</c> if there are no handlers.
        /// </summary>
        private List<EventReceiver> eventReceivers;

        private Task readLinesTask;
        private long nextId = 1;
        private JsonRpcDisconnectedEventArgs disconnectedEventArgs;
        private bool startedListening;

        /// <summary>
        /// Backing field for the <see cref="TraceSource"/> property.
        /// </summary>
        private TraceSource traceSource = new TraceSource(nameof(JsonRpc));

        /// <summary>
        /// Backing field for the <see cref="CancelLocallyInvokedMethodsWhenConnectionIsClosed"/> property.
        /// </summary>
        private bool cancelLocallyInvokedMethodsWhenConnectionIsClosed;

        /// <summary>
        /// Backing field for the <see cref="SynchronizationContext"/> property.
        /// </summary>
        private SynchronizationContext synchronizationContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
        /// <see cref="HeaderDelimitedMessageHandler"/> around messages serialized using the
        /// <see cref="JsonMessageFormatter"/>.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <remarks>
        /// It is important to call <see cref="StartListening"/> to begin receiving messages.
        /// </remarks>
        public JsonRpc(Stream sendingStream, Stream receivingStream, object target = null)
            : this(new HeaderDelimitedMessageHandler(sendingStream, receivingStream, new JsonMessageFormatter()))
        {
            if (target != null)
            {
                this.AddLocalRpcTarget(target);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class.
        /// </summary>
        /// <param name="messageHandler">The message handler to use to transmit and receive RPC messages.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <remarks>
        /// It is important to call <see cref="StartListening"/> to begin receiving messages.
        /// </remarks>
        public JsonRpc(IJsonRpcMessageHandler messageHandler, object target)
            : this(messageHandler)
        {
            if (target != null)
            {
                this.AddLocalRpcTarget(target);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class.
        /// </summary>
        /// <param name="messageHandler">The message handler to use to transmit and receive RPC messages.</param>
        /// <remarks>
        /// It is important to call <see cref="StartListening"/> to begin receiving messages.
        /// </remarks>
        public JsonRpc(IJsonRpcMessageHandler messageHandler)
        {
            Requires.NotNull(messageHandler, nameof(messageHandler));

            this.cancelPendingOutboundRequestAction = this.CancelPendingOutboundRequest;
            this.handleInvocationTaskResultDelegate = (t, request) => this.HandleInvocationTaskResult((JsonRpcRequest)request, t);

            this.MessageHandler = messageHandler;
        }

        /// <summary>
        /// Raised when the underlying stream is disconnected.
        /// </summary>
        public event EventHandler<JsonRpcDisconnectedEventArgs> Disconnected
        {
            add
            {
                Requires.NotNull(value, nameof(value));
                JsonRpcDisconnectedEventArgs disconnectedArgs;
                lock (this.disconnectedEventLock)
                {
                    disconnectedArgs = this.disconnectedEventArgs;
                    if (disconnectedArgs == null)
                    {
                        this.DisconnectedPrivate += value;
                    }
                }

                if (disconnectedArgs != null)
                {
                    value(this, disconnectedArgs);
                }
            }

            remove
            {
                Requires.NotNull(value, nameof(value));
                this.DisconnectedPrivate -= value;
            }
        }

        private event EventHandler<JsonRpcDisconnectedEventArgs> DisconnectedPrivate;

        /// <summary>
        /// Event IDs raised to our <see cref="TraceSource"/>.
        /// </summary>
        public enum TraceEvents
        {
            /// <summary>
            /// Occurs when a local RPC method is added to our mapping table.
            /// </summary>
            LocalMethodAdded,

            /// <summary>
            /// Occurs when a candidate local RPC method is NOT added to our mapping table.
            /// </summary>
            LocalMethodNotAdded,

            /// <summary>
            /// Occurs when an event handler subscribes to an event on an added target object.
            /// </summary>
            LocalEventListenerAdded,

            /// <summary>
            /// Occurs when this instance starts listening for incoming RPC messages.
            /// </summary>
            ListeningStarted,

            /// <summary>
            /// Occurs when a notification arrives that is attempting to cancel a prior request.
            /// </summary>
            ReceivedCancellation,

            /// <summary>
            /// Occurs when a JSON-RPC request or notification was received, but no local method is found to invoke for it.
            /// </summary>
            RequestWithoutMatchingTarget,

            /// <summary>
            /// Occurs when a <see cref="JsonRpcRequest"/> is received.
            /// </summary>
            RequestReceived,

            /// <summary>
            /// Occurs when any <see cref="JsonRpcMessage"/> is received.
            /// At <see cref="System.Diagnostics.TraceLevel.Info"/>, <see cref="TraceListener.TraceData(TraceEventCache, string, TraceEventType, int, object)"/>
            /// is invoked with the <see cref="JsonRpcMessage"/> that is received.
            /// At <see cref="System.Diagnostics.TraceLevel.Verbose"/>, <see cref="TraceListener.TraceEvent(TraceEventCache, string, TraceEventType, int, string, object[])"/>
            /// is invoked with the JSON representation of the message.
            /// </summary>
            MessageReceived,

            /// <summary>
            /// Occurs when any <see cref="JsonRpcMessage"/> is transmitted.
            /// At <see cref="System.Diagnostics.TraceLevel.Info"/>, <see cref="TraceListener.TraceData(TraceEventCache, string, TraceEventType, int, object)"/>
            /// is invoked with the <see cref="JsonRpcMessage"/> that is transmitted.
            /// At <see cref="System.Diagnostics.TraceLevel.Verbose"/>, <see cref="TraceListener.TraceEvent(TraceEventCache, string, TraceEventType, int, string, object[])"/>
            /// is invoked with the JSON representation of the message.
            /// </summary>
            MessageSent,

            /// <summary>
            /// Occurs when a <see cref="JsonRpcRequest"/> is received and successfully mapped to a local method to be invoked.
            /// </summary>
            LocalInvocation,

            /// <summary>
            /// Occurs when a locally invoked method from a <see cref="JsonRpcRequest"/> throws an exception (or returns a faulted <see cref="Task"/>).
            /// <see cref="TraceListener.TraceData(TraceEventCache, string, TraceEventType, int, object[])"/> is invoked with the thrown <see cref="Exception"/>, request method name, request ID, and the argument object/array.
            /// <see cref="TraceListener.TraceEvent(TraceEventCache, string, TraceEventType, int, string, object[])"/> is invoked with a text message formatted with exception information.
            /// </summary>
            LocalInvocationError,

            /// <summary>
            /// Occurs when a successful result message for a prior invocation is received.
            /// </summary>
            ReceivedResult,

            /// <summary>
            /// Occurs when an error message for a prior invocation is received.
            /// </summary>
            ReceivedError,

            /// <summary>
            /// Occurs when the connection is closed.
            /// </summary>
            Closed,

            /// <summary>
            /// A local request is canceled because the remote party terminated the connection.
            /// </summary>
            RequestAbandonedByRemote,

            /// <summary>
            /// An extensibility point was leveraged locally and broke the contract.
            /// </summary>
            LocalContractViolation,
        }

        /// <summary>
        /// Gets or sets the <see cref="System.Threading.SynchronizationContext"/> to use when invoking methods requested by the remote party.
        /// </summary>
        /// <value>Defaults to null.</value>
        /// <remarks>
        /// When not specified, methods are invoked on the threadpool.
        /// </remarks>
        public SynchronizationContext SynchronizationContext
        {
            get => this.synchronizationContext;

            set
            {
                this.ThrowIfConfigurationLocked();
                this.synchronizationContext = value;
            }
        }

        /// <summary>
        /// Gets a <see cref="Task"/> that completes when this instance is disposed or when listening has stopped
        /// whether by error, disposal or the stream closing.
        /// </summary>
        /// <remarks>
        /// The returned <see cref="Task"/> may transition to a faulted state
        /// for exceptions fatal to the protocol or this instance.
        /// </remarks>
        public Task Completion
        {
            get
            {
                return this.completionSource.Task;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether configuration of this instance
        /// can be changed after <see cref="StartListening"/> or <see cref="Attach(Stream, object)"/>
        /// has been called.
        /// </summary>
        /// <value>The default is <c>false</c>.</value>
        /// <remarks>
        /// By default, all configuration such as target objects and target methods must be set
        /// before listening starts to avoid a race condition whereby we receive a method invocation
        /// message before we have wired up a handler for it and must reject the call.
        /// But in some advanced scenarios, it may be necessary to add target methods after listening
        /// has started (e.g. in response to an invocation that enables additional functionality),
        /// in which case setting this property to <c>true</c> is appropriate.
        /// </remarks>
        public bool AllowModificationWhileListening { get; set; }

        /// <inheritdoc />
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether to cancel all methods dispatched locally
        /// that accept a <see cref="CancellationToken"/> when the connection with the remote party is closed.
        /// </summary>
        public bool CancelLocallyInvokedMethodsWhenConnectionIsClosed
        {
            get => this.cancelLocallyInvokedMethodsWhenConnectionIsClosed;
            set
            {
                // We don't typically allow changing this setting after listening has started because
                // it would not have applied to requests that have already come in. Folks should opt in
                // to that otherwise non-deterministic behavior, or simply set it before listening starts.
                this.ThrowIfConfigurationLocked();
                this.cancelLocallyInvokedMethodsWhenConnectionIsClosed = value;
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="System.Diagnostics.TraceSource"/> used to trace JSON-RPC messages and events.
        /// </summary>
        /// <value>The value can never be null.</value>
        /// <exception cref="ArgumentNullException">Thrown by the setter if a null value is provided.</exception>
        public TraceSource TraceSource
        {
            get => this.traceSource;
            set
            {
                Requires.NotNull(value, nameof(value));
                this.traceSource = value;
            }
        }

        /// <summary>
        /// Gets the message handler used to send and receive messages.
        /// </summary>
        internal IJsonRpcMessageHandler MessageHandler { get; }

        /// <summary>
        /// Gets a token that is cancelled when the connection is lost.
        /// </summary>
        internal CancellationToken DisconnectedToken => this.disconnectedSource.Token;

        /// <summary>
        /// Gets the user-specified <see cref="SynchronizationContext"/> or a default instance that will execute work on the threadpool.
        /// </summary>
        private SynchronizationContext SynchronizationContextOrDefault => this.SynchronizationContext ?? DefaultSynchronizationContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
        /// <see cref="HeaderDelimitedMessageHandler"/> around messages serialized using the
        /// <see cref="JsonMessageFormatter"/>, and immediately starts listening.
        /// </summary>
        /// <param name="stream">A bidirectional stream to send and receive RPC messages on.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <returns>The initialized and listening <see cref="JsonRpc"/> object.</returns>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public static JsonRpc Attach(Stream stream, object target = null)
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        {
            Requires.NotNull(stream, nameof(stream));

            return Attach(stream, stream, target);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
        /// <see cref="HeaderDelimitedMessageHandler"/> around messages serialized using the
        /// <see cref="JsonMessageFormatter"/>, and immediately starts listening.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <returns>The initialized and listening <see cref="JsonRpc"/> object.</returns>
        public static JsonRpc Attach(Stream sendingStream, Stream receivingStream, object target = null)
        {
            if (sendingStream == null && receivingStream == null)
            {
                throw new ArgumentException(Resources.BothReadableWritableAreNull);
            }

            var rpc = new JsonRpc(sendingStream, receivingStream, target);
            try
            {
                if (receivingStream != null)
                {
                    rpc.StartListening();
                }

                return rpc;
            }
            catch
            {
                rpc.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
        /// <param name="stream">The bidirectional stream used to send and receive JSON-RPC messages.</param>
        /// <returns>
        /// An instance of the generated proxy.
        /// In addition to implementing <typeparamref name="T"/>, it also implements <see cref="IDisposable"/>
        /// and should be disposed of to close the connection.
        /// </returns>
        public static T Attach<T>(Stream stream)
            where T : class
        {
            return Attach<T>(stream, stream);
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <returns>
        /// An instance of the generated proxy.
        /// In addition to implementing <typeparamref name="T"/>, it also implements <see cref="IDisposable"/>
        /// and should be disposed of to close the connection.
        /// </returns>
        public static T Attach<T>(Stream sendingStream, Stream receivingStream)
            where T : class
        {
            var proxyType = ProxyGeneration.Get(typeof(T).GetTypeInfo(), disposable: true);
            var rpc = new JsonRpc(sendingStream, receivingStream);
            T proxy = (T)Activator.CreateInstance(proxyType.AsType(), rpc, JsonRpcProxyOptions.Default);
            rpc.StartListening();
            return proxy;
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
        /// <returns>An instance of the generated proxy.</returns>
        public T Attach<T>()
            where T : class
        {
            return this.Attach<T>(null);
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
        /// <param name="options">A set of customizations for how the client proxy is wired up. If <c>null</c>, default options will be used.</param>
        /// <returns>An instance of the generated proxy.</returns>
        public T Attach<T>(JsonRpcProxyOptions options)
            where T : class
        {
            var proxyType = ProxyGeneration.Get(typeof(T).GetTypeInfo(), disposable: false);
            T proxy = (T)Activator.CreateInstance(proxyType.AsType(), this, options ?? JsonRpcProxyOptions.Default);

            return proxy;
        }

        /// <summary>
        /// Adds the specified target as possible object to invoke when incoming messages are received.  The target object
        /// should not inherit from each other and are invoked in the order which they are added.
        /// </summary>
        /// <param name="target">Target to invoke when incoming messages are received.</param>
        public void AddLocalRpcTarget(object target) => this.AddLocalRpcTarget(target, null);

        /// <summary>
        /// Adds the specified target as possible object to invoke when incoming messages are received.  The target object
        /// should not inherit from each other and are invoked in the order which they are added.
        /// </summary>
        /// <param name="target">Target to invoke when incoming messages are received.</param>
        /// <param name="options">A set of customizations for how the target object is registered. If <c>null</c>, default options will be used.</param>
        public void AddLocalRpcTarget(object target, JsonRpcTargetOptions options)
        {
            Requires.NotNull(target, nameof(target));
            options = options ?? JsonRpcTargetOptions.Default;
            this.ThrowIfConfigurationLocked();

            var mapping = GetRequestMethodToClrMethodMap(target, options);
            lock (this.syncObject)
            {
                foreach (var item in mapping)
                {
                    string rpcMethodName = options.MethodNameTransform != null ? options.MethodNameTransform(item.Key) : item.Key;
                    Requires.Argument(rpcMethodName != null, nameof(options), nameof(JsonRpcTargetOptions.MethodNameTransform) + " delegate returned a value that is not a legal RPC method name.");
                    if (this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out var existingList))
                    {
                        // Only add methods that do not have equivalent signatures to what we already have.
                        foreach (var newMethod in item.Value)
                        {
                            if (!existingList.Any(e => e.Signature.Equals(newMethod.Signature)))
                            {
                                this.TraceLocalMethodAdded(rpcMethodName, newMethod);
                                existingList.Add(newMethod);
                            }
                            else
                            {
                                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                                {
                                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalMethodAdded, "Skipping local RPC method \"{0}\" -> {1} because a method with a colliding signature has already been added.", rpcMethodName, newMethod);
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var newMethod in item.Value)
                        {
                            this.TraceLocalMethodAdded(rpcMethodName, newMethod);
                        }

                        this.targetRequestMethodToClrMethodMap.Add(rpcMethodName, item.Value);
                    }
                }

                if (options.NotifyClientOfEvents)
                {
                    foreach (var evt in target.GetType().GetTypeInfo().DeclaredEvents)
                    {
                        if (evt.AddMethod.IsPublic && !evt.AddMethod.IsStatic)
                        {
                            if (this.eventReceivers == null)
                            {
                                this.eventReceivers = new List<EventReceiver>();
                            }

                            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                            {
                                this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalEventListenerAdded, "Listening for events from {0}.{1} to raise notification.", target.GetType().FullName, evt.Name);
                            }

                            this.eventReceivers.Add(new EventReceiver(this, target, evt, options));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adds a handler for an RPC method with a given name.
        /// </summary>
        /// <param name="rpcMethodName">
        /// The name of the method as it is identified by the incoming JSON-RPC message.
        /// It need not match the name of the CLR method/delegate given here.
        /// </param>
        /// <param name="handler">
        /// The method or delegate to invoke when a matching RPC message arrives.
        /// This method may accept parameters from the incoming JSON-RPC message.
        /// </param>
        public void AddLocalRpcMethod(string rpcMethodName, Delegate handler)
        {
            this.AddLocalRpcMethod(rpcMethodName, handler.GetMethodInfo(), handler.Target);
        }

        /// <summary>
        /// Adds a handler for an RPC method with a given name.
        /// </summary>
        /// <param name="rpcMethodName">
        /// The name of the method as it is identified by the incoming JSON-RPC message.
        /// It need not match the name of the CLR method/delegate given here.
        /// </param>
        /// <param name="handler">
        /// The method or delegate to invoke when a matching RPC message arrives.
        /// This method may accept parameters from the incoming JSON-RPC message.
        /// </param>
        /// <param name="target">An instance of the type that defines <paramref name="handler"/> which should handle the invocation.</param>
        public void AddLocalRpcMethod(string rpcMethodName, MethodInfo handler, object target)
        {
            Requires.NotNullOrEmpty(rpcMethodName, nameof(rpcMethodName));
            Requires.NotNull(handler, nameof(handler));
            Requires.Argument(handler.IsStatic == (target == null), nameof(target), Resources.TargetObjectAndMethodStaticFlagMismatch);

            this.ThrowIfConfigurationLocked();
            lock (this.syncObject)
            {
                var methodTarget = new MethodSignatureAndTarget(handler, target);
                this.TraceLocalMethodAdded(rpcMethodName, methodTarget);
                if (this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out var existingList))
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

        /// <summary>
        /// Starts listening to incoming messages.
        /// </summary>
        public void StartListening()
        {
            this.startedListening = true;

            Verify.Operation(this.MessageHandler.CanRead, Resources.StreamMustBeReadable);
            Verify.Operation(this.readLinesTask == null, Resources.InvalidAfterListenHasStarted);
            Verify.NotDisposed(this);
            this.readLinesTask = Task.Run(this.ReadAndHandleRequestsAsync, this.DisconnectedToken);
        }

        /// <summary>
        /// Invoke a method on the server.
        /// </summary>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="argument">Method argument, must be serializable to JSON.</param>
        /// <returns>A task that completes when the server method executes.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the server indicates completion of the method.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task InvokeAsync(string targetName, object argument)
        {
            return this.InvokeAsync<object>(targetName, argument);
        }

        /// <summary>
        /// Invoke a method on the server.
        /// </summary>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="arguments">Method arguments, must be serializable to JSON.</param>
        /// <returns>A task that completes when the server method executes.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the server indicates completion of the method.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task InvokeAsync(string targetName, params object[] arguments)
        {
            return this.InvokeAsync<object>(targetName, arguments);
        }

        /// <summary>
        /// Invoke a method on the server and get back the result.
        /// </summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="argument">Method argument, must be serializable to JSON.</param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the result gets back from the server.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task<TResult> InvokeAsync<TResult>(string targetName, object argument)
        {
            var arguments = new object[] { argument };

            return this.InvokeWithCancellationAsync<TResult>(targetName, arguments, CancellationToken.None);
        }

        /// <summary>
        /// Invoke a method on the server and get back the result.
        /// </summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="arguments">Method arguments, must be serializable to JSON.</param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the result gets back from the server.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task<TResult> InvokeAsync<TResult>(string targetName, params object[] arguments)
        {
            // If somebody calls InvokeInternal<T>(id, "method", null), the null is not passed as an item in the array.
            // Instead, the compiler thinks that the null is the array itself and it'll pass null directly.
            // To account for this case, we check for null below.
            arguments = arguments ?? new object[] { null };

            return this.InvokeWithCancellationAsync<TResult>(targetName, arguments, CancellationToken.None);
        }

        /// <summary>
        /// Invoke a method on the server.  The parameter is passed as an object.
        /// </summary>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="argument">Method argument, must be serializable to JSON.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the result gets back from the server.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task InvokeWithParameterObjectAsync(string targetName, object argument = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return this.InvokeWithParameterObjectAsync<object>(targetName, argument, cancellationToken);
        }

        /// <summary>
        /// Invoke a method on the server and get back the result.  The parameter is passed as an object.
        /// </summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="argument">Method argument, must be serializable to JSON.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the result gets back from the server.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object argument = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // If argument is null, this indicates that the method does not take any parameters.
            object[] argumentToPass = argument == null ? null : new object[] { argument };
            long id = Interlocked.Increment(ref this.nextId);
            return this.InvokeCoreAsync<TResult>(id, targetName, argumentToPass, cancellationToken, isParameterObject: true);
        }

        /// <summary>
        /// Invoke a method on the server.
        /// </summary>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="arguments">Method arguments, must be serializable to JSON.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <returns>A task that completes when the server method executes.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the result gets back from the server
        /// or in response to the <paramref name="cancellationToken"/> being canceled.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception,
        /// which may occur in response to the <paramref name="cancellationToken"/> being canceled.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object> arguments = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return this.InvokeWithCancellationAsync<object>(targetName, arguments, cancellationToken);
        }

        /// <summary>
        /// Invoke a method on the server and get back the result.
        /// </summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="arguments">Method arguments, must be serializable to JSON.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <exception cref="OperationCanceledException">
        /// Result task fails with this exception if the communication channel ends before the result gets back from the server
        /// or in response to the <paramref name="cancellationToken"/> being canceled.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Result task fails with this exception if the server method throws an exception,
        /// which may occur in response to the <paramref name="cancellationToken"/> being canceled.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Result task fails with this exception if the <paramref name="targetName"/> method has not been registered on the server.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object> arguments = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            long id = Interlocked.Increment(ref this.nextId);
            return this.InvokeCoreAsync<TResult>(id, targetName, arguments, cancellationToken);
        }

        /// <summary>
        /// Invoke a method on the server and don't wait for its completion, fire-and-forget style.
        /// </summary>
        /// <remarks>
        /// Any error that happens on the server side is ignored.
        /// </remarks>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="argument">Method argument, must be serializable to JSON.</param>
        /// <returns>A task that completes when the notify request is sent to the channel to the server.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task NotifyAsync(string targetName, object argument)
        {
            var arguments = new object[] { argument };

            int? id = null;
            return this.InvokeCoreAsync<object>(id, targetName, arguments, CancellationToken.None);
        }

        /// <summary>
        /// Invoke a method on the server and don't wait for its completion, fire-and-forget style.
        /// </summary>
        /// <remarks>
        /// Any error that happens on the server side is ignored.
        /// </remarks>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="arguments">Method arguments, must be serializable to JSON.</param>
        /// <returns>A task that completes when the notify request is sent to the channel to the server.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task NotifyAsync(string targetName, params object[] arguments)
        {
            int? id = null;
            return this.InvokeCoreAsync<object>(id, targetName, arguments, CancellationToken.None);
        }

        /// <summary>
        /// Invoke a method on the server and don't wait for its completion, fire-and-forget style.  The parameter is passed as an object.
        /// </summary>
        /// <remarks>
        /// Any error that happens on the server side is ignored.
        /// </remarks>
        /// <param name="targetName">The name of the method to invoke on the server. Must not be null or empty string.</param>
        /// <param name="argument">Method argument, must be serializable to JSON.</param>
        /// <returns>A task that completes when the notify request is sent to the channel to the server.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task NotifyWithParameterObjectAsync(string targetName, object argument = null)
        {
            // If argument is null, this indicates that the method does not take any parameters.
            object[] argumentToPass = argument == null ? null : new object[] { argument };

            int? id = null;

            return this.InvokeCoreAsync<object>(id, targetName, argumentToPass, CancellationToken.None, isParameterObject: true);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes managed and native resources held by this instance.
        /// </summary>
        /// <param name="disposing"><c>true</c> if being disposed; <c>false</c> if being finalized.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.IsDisposed)
            {
                this.IsDisposed = true;
                if (disposing)
                {
                    var disconnectedEventArgs = new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.LocallyDisposed);
                    this.OnJsonRpcDisconnected(disconnectedEventArgs);
                }
            }
        }

        /// <summary>
        /// Indicates whether the connection should be closed when the server throws an exception.
        /// </summary>
        /// <param name="ex">The <see cref="Exception"/> thrown from server that is potentially fatal.</param>
        /// <returns>A <see cref="bool"/> indicating if the streams should be closed.</returns>
        /// <remarks>
        /// This method is invoked within the context of an exception filter or when a task fails to complete and simply returns false by default.
        /// If the process should crash on an exception,
        /// calling <see cref="Environment.FailFast(string, Exception)"/> will produce such behavior.
        /// </remarks>
        protected virtual bool IsFatalException(Exception ex) => false;

        /// <summary>
        /// Creates the <see cref="JsonRpcError.ErrorDetail"/> to be used as the value for the error property to be sent back to the client in response to an exception being thrown from an RPC method invoked locally.
        /// </summary>
        /// <param name="request">The request that led to the invocation that ended up failing.</param>
        /// <param name="exception">The exception thrown from the RPC method.</param>
        /// <returns>The error details to return to the client. Must not be <c>null</c>.</returns>
        /// <remarks>
        /// This method may be overridden in a derived class to change the way error details are expressed.
        /// </remarks>
        protected virtual JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
        {
            var localRpcEx = exception as LocalRpcException;
            return new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode?)localRpcEx?.ErrorCode ?? JsonRpcErrorCode.InvocationError,
                Message = exception.Message,
                Data = localRpcEx != null ? localRpcEx.ErrorData : new CommonErrorData(exception),
            };
        }

        /// <summary>
        /// Invokes the specified RPC method.
        /// </summary>
        /// <typeparam name="TResult">RPC method return type.</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName">Name of the method to invoke.</param>
        /// <param name="arguments">Arguments to pass to the invoked method. If null, no arguments are passed.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <returns>A task whose result is the deserialized response from the JSON-RPC server.</returns>
        protected Task<TResult> InvokeCoreAsync<TResult>(long? id, string targetName, IReadOnlyList<object> arguments, CancellationToken cancellationToken)
        {
            return this.InvokeCoreAsync<TResult>(id, targetName, arguments, cancellationToken, isParameterObject: false);
        }

        /// <summary>
        /// Invokes the specified RPC method.
        /// </summary>
        /// <typeparam name="TResult">RPC method return type.</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName">Name of the method to invoke.</param>
        /// <param name="arguments">Arguments to pass to the invoked method. If null, no arguments are passed.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <param name="isParameterObject">Value which indicates if parameter should be passed as an object.</param>
        /// <returns>A task whose result is the deserialized response from the JSON-RPC server.</returns>
        protected async Task<TResult> InvokeCoreAsync<TResult>(long? id, string targetName, IReadOnlyList<object> arguments, CancellationToken cancellationToken, bool isParameterObject)
        {
            Requires.NotNullOrEmpty(targetName, nameof(targetName));

            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            var request = new JsonRpcRequest
            {
                Id = id,
                Method = targetName,
            };
            if (isParameterObject)
            {
                object argument = arguments;
                if (argument != null)
                {
                    if (arguments.Count != 1 || arguments[0] == null || !arguments[0].GetType().GetTypeInfo().IsClass)
                    {
                        throw new ArgumentException(Resources.ParameterNotObject);
                    }

                    argument = arguments[0];
                }

                request.Arguments = argument;
            }
            else
            {
                request.Arguments = arguments ?? EmptyObjectArray;
            }

            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.DisconnectedToken))
                {
                    if (!request.IsResponseExpected)
                    {
                        await this.TransmitAsync(request, cts.Token).ConfigureAwait(false);
                        return default;
                    }

                    Verify.Operation(this.readLinesTask != null, Resources.InvalidBeforeListenHasStarted);
                    var tcs = new TaskCompletionSource<TResult>();
                    Action<JsonRpcMessage> dispatcher = (response) =>
                    {
                        lock (this.dispatcherMapLock)
                        {
                            this.resultDispatcherMap.Remove(id.Value);
                        }

                        try
                        {
                            if (response == null)
                            {
                                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                                {
                                    this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestAbandonedByRemote, "Aborting pending request \"{0}\" because the connection was lost.", id);
                                }

                                tcs.TrySetException(new ConnectionLostException());
                            }
                            else if (response is JsonRpcError error)
                            {
                                if (error.Error?.Code == JsonRpcErrorCode.RequestCanceled)
                                {
                                    tcs.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
                                }
                                else
                                {
                                    tcs.TrySetException(CreateExceptionFromRpcError(error, targetName));
                                }
                            }
                            else if (response is JsonRpcResult result)
                            {
                                tcs.TrySetResult(result.GetResult<TResult>());
                            }
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    };

                    var callData = new OutstandingCallData(tcs, dispatcher);
                    lock (this.dispatcherMapLock)
                    {
                        this.resultDispatcherMap.Add(id.Value, callData);
                    }

                    try
                    {
                        await this.TransmitAsync(request, cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Since we aren't expecting a response to this request, clear out our memory of it to avoid a memory leak.
                        lock (this.dispatcherMapLock)
                        {
                            this.resultDispatcherMap.Remove(id.Value);
                        }

                        throw;
                    }

                    // Arrange for sending a cancellation message if canceled while we're waiting for a response.
                    using (cancellationToken.Register(this.cancelPendingOutboundRequestAction, id.Value, useSynchronizationContext: false))
                    {
                        // This task will be completed when the Response object comes back from the other end of the pipe
                        return await tcs.Task.ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException ex) when (this.DisconnectedToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectionLostException(Resources.ConnectionDropped, ex);
            }
        }

        /// <summary>
        /// Creates a dictionary which maps a request method name to its clr method name via <see cref="JsonRpcMethodAttribute" /> value.
        /// </summary>
        /// <param name="target">Object to reflect over and analyze its methods.</param>
        /// <param name="options">The options that apply for this target object.</param>
        /// <returns>Dictionary which maps a request method name to its clr method name.</returns>
        private static Dictionary<string, List<MethodSignatureAndTarget>> GetRequestMethodToClrMethodMap(object target, JsonRpcTargetOptions options)
        {
            Requires.NotNull(target, nameof(target));
            Requires.NotNull(options, nameof(options));

            var clrMethodToRequestMethodMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestMethodToClrMethodNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestMethodToDelegateMap = new Dictionary<string, List<MethodSignatureAndTarget>>(StringComparer.Ordinal);
            var candidateAliases = new Dictionary<string, string>(StringComparer.Ordinal);

            var mapping = new MethodNameMap(target.GetType().GetTypeInfo());

            for (TypeInfo t = target.GetType().GetTypeInfo(); t != null && t != typeof(object).GetTypeInfo(); t = t.BaseType?.GetTypeInfo())
            {
                // As we enumerate methods, skip accessor methods
                foreach (MethodInfo method in t.DeclaredMethods.Where(m => !m.IsSpecialName))
                {
                    if (!options.AllowNonPublicInvocation && !method.IsPublic)
                    {
                        continue;
                    }

                    var requestName = mapping.GetRpcMethodName(method);

                    if (!requestMethodToDelegateMap.TryGetValue(requestName, out var methodTargetList))
                    {
                        methodTargetList = new List<MethodSignatureAndTarget>();
                        requestMethodToDelegateMap.Add(requestName, methodTargetList);
                    }

                    // Verify that all overloads of this CLR method also claim the same request method name.
                    if (clrMethodToRequestMethodMap.TryGetValue(method.Name, out string previousRequestNameUse))
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
                    if (requestMethodToClrMethodNameMap.TryGetValue(requestName, out string previousClrNameUse))
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

                    // Skip this method if its signature matches one from a derived type we have already scanned.
                    MethodSignatureAndTarget methodTarget = new MethodSignatureAndTarget(method, target);
                    if (methodTargetList.Contains(methodTarget))
                    {
                        continue;
                    }

                    methodTargetList.Add(methodTarget);

                    // If no explicit attribute has been applied, and the method ends with Async,
                    // register a request method name that does not include Async as well.
                    var attribute = mapping.FindAttribute(method);
                    if (attribute == null && method.Name.EndsWith(ImpliedMethodNameAsyncSuffix, StringComparison.Ordinal))
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
            foreach (var candidateAlias in candidateAliases)
            {
                if (!requestMethodToClrMethodNameMap.ContainsKey(candidateAlias.Key))
                {
                    requestMethodToClrMethodNameMap.Add(candidateAlias.Key, candidateAlias.Value);
                    requestMethodToDelegateMap[candidateAlias.Key] = requestMethodToDelegateMap[candidateAlias.Value].ToList();
                }
            }

            return requestMethodToDelegateMap;
        }

        private static RemoteRpcException CreateExceptionFromRpcError(JsonRpcError response, string targetName)
        {
            Requires.NotNull(response, nameof(response));

            switch (response.Error.Code)
            {
                case JsonRpcErrorCode.InvalidParams:
                case JsonRpcErrorCode.MethodNotFound:
                    return new RemoteMethodNotFoundException(response.Error.Message, targetName);

                default:
                    return new RemoteInvocationException(response.Error.Message, (int)response.Error.Code, response.Error.Data);
            }
        }

        private static Exception StripExceptionToInnerException(Exception exception)
        {
            if (exception is TargetInvocationException || (exception is AggregateException && exception.InnerException != null))
            {
                // Never let the outer (TargetInvocationException) escape because the inner is the interesting one to the caller, the outer is due to
                // the fact that we are using reflection.
                return exception.InnerException;
            }

            return exception;
        }

        /// <summary>
        /// Extracts the literal <see cref="Task{T}"/> type from the type hierarchy of a given type.
        /// </summary>
        /// <param name="taskTypeInfo">The original type of the value returned from an RPC-invoked method.</param>
        /// <param name="taskOfTTypeInfo">Receives the <see cref="Task{T}"/> type that is a base type of <paramref name="taskTypeInfo"/>, if found.</param>
        /// <returns><c>true</c> if <see cref="Task{T}"/> could be found in the type hierarchy; otherwise <c>false</c>.</returns>
        private static bool TryGetTaskOfTType(TypeInfo taskTypeInfo, out TypeInfo taskOfTTypeInfo)
        {
            Requires.NotNull(taskTypeInfo, nameof(taskTypeInfo));

            while (taskTypeInfo != null)
            {
                if (IsTaskOfT(taskTypeInfo))
                {
                    taskOfTTypeInfo = taskTypeInfo;
                    return true;
                }

                taskTypeInfo = taskTypeInfo.BaseType?.GetTypeInfo();
            }

            taskOfTTypeInfo = null;
            return false;

            bool IsTaskOfT(TypeInfo typeInfo) => typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition() == typeof(Task<>);
        }

        private JsonRpcError CreateError(JsonRpcRequest request, Exception exception)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(exception, nameof(exception));

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Error))
            {
                this.TraceSource.TraceEvent(TraceEventType.Error, (int)TraceEvents.LocalInvocationError, "Exception thrown from request \"{0}\" for method {1}: {2}", request.Id, request.Method, exception);
                this.TraceSource.TraceData(TraceEventType.Error, (int)TraceEvents.LocalInvocationError, exception, request.Method, request.Id, request.Arguments);
            }

            exception = StripExceptionToInnerException(exception);

            var errorDetails = this.CreateErrorDetails(request, exception);
            if (errorDetails == null)
            {
                string errorMessage = $"The {this.GetType().Name}.{nameof(this.CreateErrorDetails)} method returned null, which is not allowed.";
                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Critical))
                {
                    this.TraceSource.TraceEvent(TraceEventType.Critical, (int)TraceEvents.LocalContractViolation, errorMessage);
                }

                var e = new JsonRpcDisconnectedEventArgs(
                    errorMessage,
                    DisconnectedReason.LocalContractViolation,
                    exception);

                this.OnJsonRpcDisconnected(e);
            }

            return new JsonRpcError
            {
                Id = request.Id,
                Error = errorDetails,
            };
        }

        private async Task<JsonRpcMessage> DispatchIncomingRequestAsync(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));

            CancellationTokenSource localMethodCancellationSource = null;
            try
            {
                TargetMethod targetMethod = null;
                lock (this.syncObject)
                {
                    if (this.targetRequestMethodToClrMethodMap.Count == 0)
                    {
                        if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestWithoutMatchingTarget, "No target methods are registered. \"{0}\" will not be invoked.", request.Method);
                        }

                        string message = string.Format(CultureInfo.CurrentCulture, Resources.DroppingRequestDueToNoTargetObject, request.Method);
                        return new JsonRpcError
                        {
                            Id = request.Id,
                            Error = new JsonRpcError.ErrorDetail
                            {
                                Code = JsonRpcErrorCode.MethodNotFound,
                                Message = message,
                            },
                        };
                    }

                    if (this.targetRequestMethodToClrMethodMap.TryGetValue(request.Method, out var candidateTargets))
                    {
                        targetMethod = new TargetMethod(request, candidateTargets);
                    }
                }

                if (targetMethod == null)
                {
                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                    {
                        this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestWithoutMatchingTarget, "No target methods are registered that match \"{0}\".", request.Method);
                    }

                    return new JsonRpcError
                    {
                        Id = request.Id,
                        Error = new JsonRpcError.ErrorDetail
                        {
                            Code = JsonRpcErrorCode.MethodNotFound,
                            Message = string.Format(CultureInfo.CurrentCulture, Resources.RpcMethodNameNotFound, request.Method),
                        },
                    };
                }
                else if (!targetMethod.IsFound)
                {
                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                    {
                        this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestWithoutMatchingTarget, "Invocation of \"{0}\" cannot occur because arguments do not match any registered target methods.", request.Method);
                    }

                    return new JsonRpcError
                    {
                        Id = request.Id,
                        Error = new JsonRpcError.ErrorDetail
                        {
                            Code = JsonRpcErrorCode.InvalidParams,
                            Message = targetMethod.LookupErrorMessage,
                        },
                    };
                }

                // Add cancelation to inboundCancellationSources before yielding to ensure that
                // it cannot be preempted by the cancellation request that would try to set it
                // Fix for https://github.com/Microsoft/vs-streamjsonrpc/issues/56
                var cancellationToken = CancellationToken.None;
                if (targetMethod.AcceptsCancellationToken && request.IsResponseExpected)
                {
                    localMethodCancellationSource = this.CancelLocallyInvokedMethodsWhenConnectionIsClosed
                        ? CancellationTokenSource.CreateLinkedTokenSource(this.DisconnectedToken)
                        : new CancellationTokenSource();
                    cancellationToken = localMethodCancellationSource.Token;
                    lock (this.dispatcherMapLock)
                    {
                        this.inboundCancellationSources.Add(request.Id, localMethodCancellationSource);
                    }
                }

                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                {
                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalInvocation, "Invoking {0}", targetMethod);
                }

                // Yield now so method invocation is async and we can proceed to handle other requests meanwhile.
                // IMPORTANT: This should be the first await in this async method,
                //            and no other await should be between this one and actually invoking the target method.
                //            This is crucial to the guarantee that method invocation order is preserved from client to server
                //            when a single-threaded SynchronizationContext is applied.
                await this.SynchronizationContextOrDefault;
                object result = targetMethod.Invoke(cancellationToken);
                if (!(result is Task resultingTask))
                {
                    return new JsonRpcResult
                    {
                        Id = request.Id,
                        Result = result,
                    };
                }

                return await resultingTask.ContinueWith(
                    this.handleInvocationTaskResultDelegate,
                    request,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).ConfigureAwait(false);
            }
            catch (Exception ex) when (!this.IsFatalException(StripExceptionToInnerException(ex)))
            {
                return this.CreateError(request, ex);
            }
            finally
            {
                if (localMethodCancellationSource != null)
                {
                    lock (this.dispatcherMapLock)
                    {
                        this.inboundCancellationSources.Remove(request.Id);
                    }

                    // Be sure to dispose the CTS because it may be linked to our long-lived disposal token
                    // and otherwise cause a memory leak.
                    localMethodCancellationSource.Dispose();
                }
            }
        }

        private JsonRpcMessage HandleInvocationTaskResult(JsonRpcRequest request, Task t)
        {
            Requires.NotNull(t, nameof(t));

            if (!t.IsCompleted)
            {
                throw new ArgumentException(Resources.TaskNotCompleted, nameof(t));
            }

            if (t.IsFaulted)
            {
                var exception = StripExceptionToInnerException(t.Exception);
                if (this.IsFatalException(exception))
                {
                    var e = new JsonRpcDisconnectedEventArgs(
                        string.Format(CultureInfo.CurrentCulture, Resources.FatalExceptionWasThrown, exception.GetType(), exception.Message),
                        DisconnectedReason.FatalException,
                        exception);

                    this.OnJsonRpcDisconnected(e);
                }

                return this.CreateError(request, t.Exception);
            }

            if (t.IsCanceled)
            {
                return new JsonRpcError
                {
                    Id = request.Id,
                    Error = new JsonRpcError.ErrorDetail
                    {
                        Code = JsonRpcErrorCode.RequestCanceled,
                        Message = Resources.TaskWasCancelled,
                    },
                };
            }

            // If t is a Task<SomeType>, it will have Result property.
            // If t is just a Task, there is no Result property on it.
            // We can't really write direct code to deal with Task<T>, since we have no idea of T in this context, so we simply use reflection to
            // read the result at runtime.
            // Make sure we're prepared for Task<T>-derived types, by walking back up to the actual type in order to find the Result property.
            object taskResult = null;
            if (TryGetTaskOfTType(t.GetType().GetTypeInfo(), out TypeInfo taskOfTTypeInfo))
            {
#pragma warning disable VSTHRD002 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
#pragma warning disable VSTHRD102 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
                const string ResultPropertyName = nameof(Task<int>.Result);
#pragma warning restore VSTHRD002
#pragma warning restore VSTHRD102

                PropertyInfo resultProperty = taskOfTTypeInfo.GetDeclaredProperty(ResultPropertyName);
                Assumes.NotNull(resultProperty);
                taskResult = resultProperty.GetValue(t);
            }

            return new JsonRpcResult
            {
                Id = request.Id,
                Result = taskResult,
            };
        }

        private void OnJsonRpcDisconnected(JsonRpcDisconnectedEventArgs eventArgs)
        {
            EventHandler<JsonRpcDisconnectedEventArgs> handlersToInvoke = null;
            lock (this.disconnectedEventLock)
            {
                if (this.disconnectedEventArgs != null)
                {
                    // Someone else has done all this work.
                    return;
                }
                else
                {
                    this.disconnectedEventArgs = eventArgs;
                    handlersToInvoke = this.DisconnectedPrivate;
                    this.DisconnectedPrivate = null;
                }
            }

            this.UnregisterEventHandlersFromTargetObjects();

            TraceEventType eventType = (eventArgs.Reason == DisconnectedReason.LocallyDisposed || eventArgs.Reason == DisconnectedReason.RemotePartyTerminated)
                ? TraceEventType.Information
                : TraceEventType.Critical;
            if (this.TraceSource.Switch.ShouldTrace(eventType))
            {
                this.TraceSource.TraceEvent(eventType, (int)TraceEvents.Closed, "Connection closing ({0}: {1}). {2}", eventArgs.Reason, eventArgs.Description, eventArgs.Exception);
            }

            try
            {
                // Fire the event first so that subscribers can interact with a non-disposed stream
                handlersToInvoke?.Invoke(this, eventArgs);
            }
            finally
            {
                // Dispose the stream and fault pending requests in the finally block
                // So this is executed even if Disconnected event handler throws.
                this.disconnectedSource.Cancel();
                (this.MessageHandler as IDisposable)?.Dispose();
                this.FaultPendingRequests();

                // Ensure the Task we may have returned from Completion is completed.
                if (eventArgs.Exception != null)
                {
                    this.completionSource.TrySetException(eventArgs.Exception);
                }
                else
                {
                    this.completionSource.TrySetResult(true);
                }
            }
        }

        private void UnregisterEventHandlersFromTargetObjects()
        {
            if (this.eventReceivers != null)
            {
                foreach (var receiver in this.eventReceivers)
                {
                    receiver.Dispose();
                }

                this.eventReceivers = null;
            }
        }

        private async Task ReadAndHandleRequestsAsync()
        {
            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.ListeningStarted, "Listening started.");

            try
            {
                while (!this.IsDisposed && !this.DisconnectedToken.IsCancellationRequested)
                {
                    JsonRpcMessage protocolMessage = null;
                    try
                    {
                        protocolMessage = await this.MessageHandler.ReadAsync(this.DisconnectedToken).ConfigureAwait(false);
                        if (protocolMessage == null)
                        {
                            this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(Resources.ReachedEndOfStream, DisconnectedReason.RemotePartyTerminated));
                            return;
                        }

                        this.TraceMessageReceived(protocolMessage);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(
                            string.Format(CultureInfo.CurrentCulture, Resources.ReadingJsonRpcStreamFailed, exception.GetType().Name, exception.Message),
                            exception is JsonException ? DisconnectedReason.ParseError : DisconnectedReason.StreamError,
                            exception));
                        return;
                    }

                    this.HandleRpcAsync(protocolMessage).Forget(); // all exceptions are handled internally
                }

                this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.LocallyDisposed));
            }
            catch (Exception ex)
            {
                this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(ex.Message, DisconnectedReason.StreamError, ex));
                throw;
            }
        }

        private async Task HandleRpcAsync(JsonRpcMessage rpc)
        {
            Requires.NotNull(rpc, nameof(rpc));
            try
            {
                if (rpc is JsonRpcRequest request)
                {
                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                    {
                        if (request.IsResponseExpected)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.RequestReceived, "Received request \"{0}\" for method \"{1}\".", request.Id, request.Method);
                        }
                        else
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.RequestReceived, "Received notification for method \"{0}\".", request.Method);
                        }
                    }

                    // We can't accept a request that requires a response if we can't write.
                    Verify.Operation(!request.IsResponseExpected || this.MessageHandler.CanWrite, Resources.StreamMustBeWriteable);

                    if (request.IsNotification && request.Method == CancelRequestSpecialMethod)
                    {
                        await this.HandleCancellationNotificationAsync(request).ConfigureAwait(false);
                        return;
                    }

                    JsonRpcMessage result = await this.DispatchIncomingRequestAsync(request).ConfigureAwait(false);

                    if (request.IsResponseExpected && !this.IsDisposed)
                    {
                        try
                        {
                            await this.TransmitAsync(result, this.DisconnectedToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (Exception exception)
                        {
                            var e = new JsonRpcDisconnectedEventArgs(
                                string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcResult, exception.GetType().Name, exception.Message),
                                DisconnectedReason.StreamError,
                                exception);

                            // Fatal error. Raise disconnected event.
                            this.OnJsonRpcDisconnected(e);
                        }
                    }
                }
                else if (rpc is IJsonRpcMessageWithId resultOrError)
                {
                    OutstandingCallData data = null;
                    lock (this.dispatcherMapLock)
                    {
                        long id = (long)resultOrError.Id;
                        if (this.resultDispatcherMap.TryGetValue(id, out data))
                        {
                            this.resultDispatcherMap.Remove(id);
                        }
                    }

                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                    {
                        if (resultOrError is JsonRpcResult result)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.ReceivedResult, "Received result for request \"{0}\".", result.Id);
                        }
                        else if (resultOrError is JsonRpcError error)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.ReceivedError, "Received error response for request {0}: {1} \"{2}\": ", error.Id, error.Error.Code, error.Error.Message);
                        }
                    }

                    if (data != null)
                    {
                        // Complete the caller's request with the response asynchronously so it doesn't delay handling of other JsonRpc messages.
                        await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                        data.CompletionHandler(rpc);
                    }
                }
                else
                {
                    // Not a request or result/error. Raise disconnected event.
                    this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(
                        Resources.UnrecognizedIncomingJsonRpc,
                        DisconnectedReason.ParseError));
                }
            }
            catch (Exception ex)
            {
                var eventArgs = new JsonRpcDisconnectedEventArgs(
                    string.Format(CultureInfo.CurrentCulture, Resources.UnexpectedErrorProcessingJsonRpc, ex.Message),
                    DisconnectedReason.ParseError,
                    ex);

                // Fatal error. Raise disconnected event.
                this.OnJsonRpcDisconnected(eventArgs);
            }
        }

        private async Task HandleCancellationNotificationAsync(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));

            if (request.TryGetArgumentByNameOrIndex("id", -1, null, out object id))
            {
                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                {
                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.ReceivedCancellation, "Cancellation request received for \"{0}\".", id);
                }

                CancellationTokenSource cts;
                lock (this.dispatcherMapLock)
                {
                    this.inboundCancellationSources.TryGetValue(id, out cts);
                }

                if (cts != null)
                {
                    // This cancellation token is the one that is passed to the server method.
                    // It may have callbacks registered on cancellation.
                    // Cancel it asynchronously to ensure that these callbacks do not delay handling of other json rpc messages.
                    await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // There is a race condition between when we retrieve the CTS and actually call Cancel,
                        // vs. another thread that disposes the CTS at the conclusion of the method invocation.
                        // It cannot be prevented, so just swallow it since the method executed successfully.
                    }
                }
            }
        }

        private void FaultPendingRequests()
        {
            OutstandingCallData[] pendingRequests;
            lock (this.dispatcherMapLock)
            {
                pendingRequests = this.resultDispatcherMap.Values.ToArray();
            }

            foreach (OutstandingCallData pendingRequest in pendingRequests)
            {
                pendingRequest.CompletionHandler(null);
            }
        }

        /// <summary>
        /// Cancels an individual outbound pending request.
        /// </summary>
        /// <param name="state">The ID associated with the request to be canceled.</param>
        private void CancelPendingOutboundRequest(object state)
        {
            Requires.NotNull(state, nameof(state));
            Task.Run(async delegate
            {
                if (!this.IsDisposed)
                {
                    var cancellationMessage = new JsonRpcRequest
                    {
                        Method = CancelRequestSpecialMethod,
                        NamedArguments = new Dictionary<string, object>
                        {
                            { "id", state },
                        },
                    };
                    await this.TransmitAsync(cancellationMessage, this.DisconnectedToken).ConfigureAwait(false);
                }
            }).Forget();
        }

        private void TraceLocalMethodAdded(string rpcMethodName, MethodSignatureAndTarget targetMethod)
        {
            Requires.NotNullOrEmpty(rpcMethodName, nameof(rpcMethodName));

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalMethodAdded, "Added local RPC method \"{0}\" -> {1}", rpcMethodName, targetMethod);
            }
        }

        private void TraceMessageSent(JsonRpcMessage message)
        {
            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceData(TraceEventType.Information, (int)TraceEvents.MessageSent, message);
            }

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
            {
                this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)TraceEvents.MessageSent, "Sent: {0}", this.GetMessageJson(message));
            }
        }

        private void TraceMessageReceived(JsonRpcMessage message)
        {
            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceData(TraceEventType.Information, (int)TraceEvents.MessageReceived, message);
            }

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
            {
                this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)TraceEvents.MessageReceived, "Received: {0}", this.GetMessageJson(message));
            }
        }

        private object GetMessageJson(JsonRpcMessage message)
        {
            try
            {
                return this.MessageHandler.Formatter.GetJsonText(message);
            }
            catch (Exception ex)
            {
                return $"<JSON representation not available: {ex.Message}>";
            }
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        private ValueTask TransmitAsync(JsonRpcMessage message, CancellationToken cancellationToken)
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
        {
            this.TraceMessageSent(message);

            return this.MessageHandler.WriteAsync(message, cancellationToken);
        }

        /// <summary>
        /// Throws an exception if we have already started listening,
        /// unless <see cref="AllowModificationWhileListening"/> is <c>true</c>.
        /// </summary>
        private void ThrowIfConfigurationLocked()
        {
            Verify.Operation(!this.startedListening || this.AllowModificationWhileListening, Resources.MustNotBeListening);
        }

        internal class MethodNameMap
        {
            private readonly List<InterfaceMapping> interfaceMaps;

            internal MethodNameMap(TypeInfo typeInfo)
            {
                Requires.NotNull(typeInfo, nameof(typeInfo));
#if !NETSTANDARD1_6
                this.interfaceMaps = typeInfo.IsInterface ? new List<InterfaceMapping>()
                    : typeInfo.ImplementedInterfaces.Select(typeInfo.GetInterfaceMap).ToList();
#else
                this.interfaceMaps = new List<InterfaceMapping>();
#endif
            }

            internal string GetRpcMethodName(MethodInfo method)
            {
                Requires.NotNull(method, nameof(method));

                return this.FindAttribute(method)?.Name ?? method.Name;
            }

            internal JsonRpcMethodAttribute FindAttribute(MethodInfo method)
            {
                Requires.NotNull(method, nameof(method));

                // Get the custom attribute, which may appear on the method itself or the interface definition of the method where applicable.
                var attribute = (JsonRpcMethodAttribute)method.GetCustomAttribute(typeof(JsonRpcMethodAttribute));
                if (attribute == null)
                {
                    attribute = (JsonRpcMethodAttribute)this.FindMethodOnInterface(method)?.GetCustomAttribute(typeof(JsonRpcMethodAttribute));
                }

                return attribute;
            }

            private MethodInfo FindMethodOnInterface(MethodInfo methodImpl)
            {
                Requires.NotNull(methodImpl, nameof(methodImpl));

                foreach (var map in this.interfaceMaps)
                {
                    int methodIndex = Array.IndexOf(map.TargetMethods, methodImpl);
                    if (methodIndex >= 0)
                    {
                        return map.InterfaceMethods[methodIndex];
                    }
                }

                return null;
            }
        }

        private class OutstandingCallData
        {
            internal OutstandingCallData(object taskCompletionSource, Action<JsonRpcMessage> completionHandler)
            {
                this.TaskCompletionSource = taskCompletionSource;
                this.CompletionHandler = completionHandler;
            }

            internal object TaskCompletionSource { get; }

            internal Action<JsonRpcMessage> CompletionHandler { get; }
        }

        private class EventReceiver : IDisposable
        {
            private static readonly MethodInfo OnEventRaisedMethodInfo = typeof(EventReceiver).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(OnEventRaised));
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
                    this.registeredHandler = OnEventRaisedMethodInfo.CreateDelegate(eventInfo.EventHandlerType, this);
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

            private void OnEventRaised(object sender, EventArgs args)
            {
                this.jsonRpc.NotifyAsync(this.rpcEventName, new object[] { args }).Forget();
            }
        }
    }
}
