// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Newtonsoft.Json;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// Manages a JSON-RPC connection with another entity over a <see cref="Stream"/>.
    /// </summary>
    public class JsonRpc : IDisposableObservable, IJsonRpcFormatterCallbacks, IJsonRpcTracingCallbacks
    {
        /// <summary>
        /// The <see cref="System.Threading.SynchronizationContext"/> to use to schedule work on the threadpool.
        /// </summary>
        internal static readonly SynchronizationContext DefaultSynchronizationContext = new SynchronizationContext();

        private const string ImpliedMethodNameAsyncSuffix = "Async";
        private static readonly MethodInfo MarshalWithControlledLifetimeOpenGenericMethodInfo = typeof(JsonRpc).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == nameof(MarshalWithControlledLifetime) && m.IsGenericMethod);

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly object syncObject = new object();

        /// <summary>
        /// The object to lock when accessing the <see cref="resultDispatcherMap"/>.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly object dispatcherMapLock = new object();

        /// <summary>
        /// The object to lock when accessing the <see cref="DisconnectedPrivate"/> member.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly object disconnectedEventLock = new object();

        /// <summary>
        /// A map of outbound calls awaiting responses.
        /// Lock the <see cref="dispatcherMapLock"/> object for all access to this member.
        /// </summary>
        private readonly Dictionary<RequestId, OutstandingCallData> resultDispatcherMap = new Dictionary<RequestId, OutstandingCallData>();

        /// <summary>
        /// A delegate for the <see cref="CancelPendingOutboundRequest"/> method.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly Action<object> cancelPendingOutboundRequestAction;

        /// <summary>
        /// A collection of target objects and their map of clr method to <see cref="JsonRpcMethodAttribute"/> values.
        /// </summary>
        private readonly Dictionary<string, List<MethodSignatureAndTarget>> targetRequestMethodToClrMethodMap = new Dictionary<string, List<MethodSignatureAndTarget>>(StringComparer.Ordinal);

        /// <summary>
        /// The source for the <see cref="DisconnectedToken"/> property.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
#pragma warning disable CA2213 // Disposable fields should be disposed
        private readonly CancellationTokenSource disconnectedSource = new CancellationTokenSource();
#pragma warning restore CA2213 // Disposable fields should be disposed

        /// <summary>
        /// The completion source behind <see cref="Completion"/>.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly TaskCompletionSource<bool> completionSource = new TaskCompletionSource<bool>();

        /// <summary>
        /// Backing field for the <see cref="DispatchCompletion"/> property.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly AsyncManualResetEvent dispatchCompletionSource = new AsyncManualResetEvent(initialState: true);

        /// <summary>
        /// List of remote RPC targets to call if connection should be relayed.
        /// </summary>
        private ImmutableList<JsonRpc> remoteRpcTargets = ImmutableList<JsonRpc>.Empty;

        /// <summary>
        /// A list of event handlers we've registered on target objects that define events. May be <c>null</c> if there are no handlers.
        /// </summary>
        private List<EventReceiver>? eventReceivers;

        private Task? readLinesTask;
        private long nextId = 1;
        private int requestsInDispatchCount;
        private JsonRpcDisconnectedEventArgs? disconnectedEventArgs;

        /// <summary>
        /// A lazily-initialized list of objects to dispose of when the JSON-RPC connection drops.
        /// </summary>
        private List<object>? localTargetObjectsToDispose;

        /// <summary>
        /// Backing field for the <see cref="TraceSource"/> property.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private TraceSource traceSource = new TraceSource(nameof(JsonRpc));

        /// <summary>
        /// Backing field for the <see cref="CancelLocallyInvokedMethodsWhenConnectionIsClosed"/> property.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool cancelLocallyInvokedMethodsWhenConnectionIsClosed;

        /// <summary>
        /// Backing field for the <see cref="SynchronizationContext"/> property.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SynchronizationContext? synchronizationContext;

        /// <summary>
        /// Backing field for the <see cref="CancellationStrategy"/> property.
        /// </summary>
        private ICancellationStrategy? cancellationStrategy;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterCallbacks.RequestTransmissionAborted"/> event.
        /// </summary>
        private EventHandler<JsonRpcMessageEventArgs>? requestTransmissionAborted;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterCallbacks.ResponseReceived"/> event.
        /// </summary>
        private EventHandler<JsonRpcResponseEventArgs>? responseReceived;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterCallbacks.ResponseSent"/> event.
        /// </summary>
        private EventHandler<JsonRpcResponseEventArgs>? responseSent;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
        /// <see cref="HeaderDelimitedMessageHandler"/> around messages serialized using the
        /// <see cref="JsonMessageFormatter"/>.
        /// </summary>
        /// <param name="stream">The full duplex stream used to transmit and receive messages.</param>
        /// <remarks>
        /// It is important to call <see cref="StartListening"/> to begin receiving messages.
        /// </remarks>
        public JsonRpc(Stream stream)
#pragma warning disable CA2000 // Dispose objects before losing scope
            : this(new HeaderDelimitedMessageHandler(Requires.NotNull(stream, nameof(stream)), stream, new JsonMessageFormatter()))
#pragma warning restore CA2000 // Dispose objects before losing scope
        {
        }

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
        public JsonRpc(Stream? sendingStream, Stream? receivingStream, object? target = null)
#pragma warning disable CA2000 // Dispose objects before losing scope
            : this(new HeaderDelimitedMessageHandler(sendingStream, receivingStream, new JsonMessageFormatter()))
#pragma warning restore CA2000 // Dispose objects before losing scope
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
        public JsonRpc(IJsonRpcMessageHandler messageHandler, object? target)
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

            if (messageHandler.Formatter is IJsonRpcInstanceContainer formatter)
            {
                formatter.Rpc = this;
            }

            this.cancelPendingOutboundRequestAction = this.CancelPendingOutboundRequest;

            this.MessageHandler = messageHandler;

            // Default to preserving order of incoming messages since so many users assume this is the behavior.
            // If ordering is not required and higher throughput is desired, the owner of this instance can clear this property
            // so that all incoming messages are queued to the threadpool, allowing immediate concurrency.
            this.SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false);
            this.CancellationStrategy = new StandardCancellationStrategy(this);
        }

        /// <summary>
        /// Raised when the underlying stream is disconnected.
        /// </summary>
        public event EventHandler<JsonRpcDisconnectedEventArgs>? Disconnected
        {
            add
            {
                JsonRpcDisconnectedEventArgs? disconnectedArgs;
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
                    value?.Invoke(this, disconnectedArgs);
                }
            }

            remove
            {
                this.DisconnectedPrivate -= value;
            }
        }

        /// <inheritdoc/>
        event EventHandler<JsonRpcMessageEventArgs> IJsonRpcFormatterCallbacks.RequestTransmissionAborted
        {
            add => this.requestTransmissionAborted += value;
            remove => this.requestTransmissionAborted -= value;
        }

        /// <inheritdoc/>
        event EventHandler<JsonRpcResponseEventArgs> IJsonRpcFormatterCallbacks.ResponseReceived
        {
            add => this.responseReceived += value;
            remove => this.responseReceived -= value;
        }

        /// <inheritdoc/>
        event EventHandler<JsonRpcResponseEventArgs> IJsonRpcFormatterCallbacks.ResponseSent
        {
            add => this.responseSent += value;
            remove => this.responseSent -= value;
        }

        private event EventHandler<JsonRpcDisconnectedEventArgs>? DisconnectedPrivate;

        /// <summary>
        /// Event IDs raised to our <see cref="TraceSource"/>.
        /// </summary>
#pragma warning disable CA1717 // Only FlagsAttribute enums should have plural names
        public enum TraceEvents
#pragma warning restore CA1717 // Only FlagsAttribute enums should have plural names
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

            /// <summary>
            /// An exception occurred when reading or writing the $/progress notification.
            /// </summary>
            ProgressNotificationError,

            /// <summary>
            /// An incoming RPC call included an argument that failed to deserialize to the type on a candidate target method's proposed matching parameter.
            /// </summary>
            /// <remarks>
            /// This may not represent a fatal error. When there are multiple overloads to choose from,
            /// choosing the overload to invoke involves attempts to deserialize arguments to the types dictated by each overload's parameters.
            /// Thus a failure recorded in this event may be followed by a successful deserialization to another parameter type and invocation of a different overload.
            /// </remarks>
            MethodArgumentDeserializationFailure,

            /// <summary>
            /// An outgoing RPC message was not sent due to an exception, possibly a serialization failure.
            /// </summary>
            TransmissionFailed,

            /// <summary>
            /// An incoming <see cref="Exception"/> cannot be deserialized to its original type because the type could not be loaded.
            /// </summary>
            ExceptionTypeNotFound,
        }

        /// <summary>
        /// Gets or sets the <see cref="System.Threading.SynchronizationContext"/> to use when invoking methods requested by the remote party.
        /// </summary>
        /// <value>Defaults to null.</value>
        /// <remarks>
        /// When not specified, methods are invoked on the threadpool.
        /// </remarks>
        public SynchronizationContext? SynchronizationContext
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
        /// <para>
        /// The returned <see cref="Task"/> may transition to a faulted state
        /// for exceptions fatal to the protocol or this instance.
        /// </para>
        /// <para>
        /// When local RPC target objects or methods have been added, those methods may still be running from prior RPC requests
        /// when this property completes. Track their completion with the <see cref="DispatchCompletion"/> property.
        /// </para>
        /// </remarks>
        public Task Completion
        {
            get
            {
                return this.completionSource.Task;
            }
        }

        /// <summary>
        /// Gets a <see cref="Task"/> that completes when no local target methods are executing from an RPC call.
        /// </summary>
        /// <remarks>
        /// If the JSON-RPC connection is still active when retrieving this property's value, the returned <see cref="Task"/> will complete
        /// when no local dispatches are in progress, even if the connection is still active.
        /// Retrieving the property after a previously obtained <see cref="Task"/> has completed will result in a new, incomplete <see cref="Task"/> if incoming requests are currently in dispatch.
        /// </remarks>
        public Task DispatchCompletion => this.dispatchCompletionSource.WaitAsync();

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
        /// Gets or sets the cancellation strategy to use.
        /// </summary>
        /// <value>The default value is the <see cref="StandardCancellationStrategy"/> which uses the "$/cancelRequest" notification.</value>
        /// <inheritdoc cref="ThrowIfConfigurationLocked" path="/exception"/>
        public ICancellationStrategy? CancellationStrategy
        {
            get => this.cancellationStrategy;
            set
            {
                this.ThrowIfConfigurationLocked();
                this.cancellationStrategy = value;
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
        /// Gets a value indicating whether listening has started.
        /// </summary>
        private bool HasListeningStarted => this.readLinesTask != null;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
        /// <see cref="HeaderDelimitedMessageHandler"/> around messages serialized using the
        /// <see cref="JsonMessageFormatter"/>, and immediately starts listening.
        /// </summary>
        /// <param name="stream">A bidirectional stream to send and receive RPC messages on.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <returns>The initialized and listening <see cref="JsonRpc"/> object.</returns>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public static JsonRpc Attach(Stream stream, object? target = null)
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
        public static JsonRpc Attach(Stream? sendingStream, Stream? receivingStream, object? target = null)
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
        public static T Attach<T>(Stream? sendingStream, Stream? receivingStream)
            where T : class
        {
            TypeInfo proxyType = ProxyGeneration.Get(typeof(T).GetTypeInfo());
            var rpc = new JsonRpc(sendingStream, receivingStream);
            T proxy = (T)Activator.CreateInstance(proxyType.AsType(), rpc, JsonRpcProxyOptions.Default, /* onDispose: */ null)!;
            rpc.StartListening();
            return proxy;
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
        /// <param name="handler">The message handler to use.</param>
        /// <returns>
        /// An instance of the generated proxy.
        /// In addition to implementing <typeparamref name="T"/>, it also implements <see cref="IDisposable"/>
        /// and should be disposed of to close the connection.
        /// </returns>
        public static T Attach<T>(IJsonRpcMessageHandler handler)
            where T : class
        {
            return Attach<T>(handler, options: null);
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
        /// <param name="handler">The message handler to use.</param>
        /// <param name="options">A set of customizations for how the client proxy is wired up. If <c>null</c>, default options will be used.</param>
        /// <returns>
        /// An instance of the generated proxy.
        /// In addition to implementing <typeparamref name="T"/>, it also implements <see cref="IDisposable"/>
        /// and should be disposed of to close the connection.
        /// </returns>
        public static T Attach<T>(IJsonRpcMessageHandler handler, JsonRpcProxyOptions? options)
            where T : class
        {
            TypeInfo proxyType = ProxyGeneration.Get(typeof(T).GetTypeInfo());
            var rpc = new JsonRpc(handler);
            T proxy = (T)Activator.CreateInstance(proxyType.AsType(), rpc, options ?? JsonRpcProxyOptions.Default, options?.OnDispose)!;
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
        public T Attach<T>(JsonRpcProxyOptions? options)
            where T : class
        {
            TypeInfo proxyType = ProxyGeneration.Get(typeof(T).GetTypeInfo());
            T proxy = (T)Activator.CreateInstance(proxyType.AsType(), this, options ?? JsonRpcProxyOptions.Default, options?.OnDispose)!;
            return proxy;
        }

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <param name="interfaceType">The interface that describes the functions available on the remote end.</param>
        /// <returns>An instance of the generated proxy.</returns>
        public object Attach(Type interfaceType) => this.Attach(interfaceType, options: null);

        /// <summary>
        /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
        /// </summary>
        /// <param name="interfaceType">The interface that describes the functions available on the remote end.</param>
        /// <param name="options">A set of customizations for how the client proxy is wired up. If <c>null</c>, default options will be used.</param>
        /// <returns>An instance of the generated proxy.</returns>
        public object Attach(Type interfaceType, JsonRpcProxyOptions? options)
        {
            Requires.NotNull(interfaceType, nameof(interfaceType));
            TypeInfo proxyType = ProxyGeneration.Get(interfaceType.GetTypeInfo());
            object proxy = Activator.CreateInstance(proxyType.AsType(), this, options ?? JsonRpcProxyOptions.Default, options?.OnDispose)!;
            return proxy;
        }

        /// <inheritdoc cref="AddLocalRpcTarget(object, JsonRpcTargetOptions?)"/>
        public void AddLocalRpcTarget(object target) => this.AddLocalRpcTarget(target, null);

        /// <inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)"/>
        public void AddLocalRpcTarget(object target, JsonRpcTargetOptions? options) => this.AddLocalRpcTarget(Requires.NotNull(target, nameof(target)).GetType(), target, options);

        /// <inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)"/>
        /// <typeparam name="T"><inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)" path="/param[@name='exposingMembersOn']"/></typeparam>
        public void AddLocalRpcTarget<T>(T target, JsonRpcTargetOptions? options)
            where T : notnull => this.AddLocalRpcTarget(typeof(T), target, options);

        /// <summary>
        /// Adds the specified target as possible object to invoke when incoming messages are received.
        /// </summary>
        /// <param name="exposingMembersOn">
        /// The type whose members define the RPC accessible members of the <paramref name="target"/> object.
        /// If this type is not an interface, only public members become invokable unless <see cref="JsonRpcTargetOptions.AllowNonPublicInvocation"/> is set to true on the <paramref name="options"/> argument.
        /// </param>
        /// <param name="target">Target to invoke when incoming messages are received.</param>
        /// <param name="options">A set of customizations for how the target object is registered. If <c>null</c>, default options will be used.</param>
        /// <remarks>
        /// When multiple target objects are added, the first target with a method that matches a request is invoked.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <c>false</c>.</exception>
        public void AddLocalRpcTarget(Type exposingMembersOn, object target, JsonRpcTargetOptions? options)
        {
            Requires.NotNull(exposingMembersOn, nameof(exposingMembersOn));
            Requires.NotNull(target, nameof(target));
            this.ThrowIfConfigurationLocked();

#pragma warning disable CA2000 // Dispose objects before losing scope
            this.AddLocalRpcTargetInternal(exposingMembersOn, target, options, requestRevertOption: false);
#pragma warning restore CA2000 // Dispose objects before losing scope
        }

        /// <summary>
        /// Adds a remote rpc connection so calls can be forwarded to the remote target if local targets do not handle it.
        /// </summary>
        /// <param name="remoteTarget">The json rpc connection to the remote target.</param>
        public void AddRemoteRpcTarget(JsonRpc remoteTarget)
        {
            Requires.NotNull(remoteTarget, nameof(remoteTarget));
            this.ThrowIfConfigurationLocked();

            lock (this.syncObject)
            {
                this.remoteRpcTargets = this.remoteRpcTargets.Add(remoteTarget);
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
        /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <c>false</c>.</exception>
        public void AddLocalRpcMethod(string? rpcMethodName, Delegate handler)
        {
            Requires.NotNull(handler, nameof(handler));
            this.AddLocalRpcMethod(rpcMethodName, handler.GetMethodInfo()!, handler.Target);
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
        /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <c>false</c>.</exception>
        public void AddLocalRpcMethod(string? rpcMethodName, MethodInfo handler, object? target) => this.AddLocalRpcMethod(handler, target, new JsonRpcMethodAttribute(rpcMethodName));

        /// <inheritdoc cref="AddLocalRpcMethod(MethodInfo, object?, JsonRpcMethodAttribute?, SynchronizationContext?)"/>
        public void AddLocalRpcMethod(MethodInfo handler, object? target, JsonRpcMethodAttribute? methodRpcSettings) => this.AddLocalRpcMethod(handler, target, methodRpcSettings, synchronizationContext: null);

        /// <summary>
        /// Gets the <see cref="JsonRpcMethodAttribute"/> for a previously discovered RPC method, if there is one.
        /// </summary>
        /// <param name="methodName">The name of the method for which the attribute is sought.</param>
        /// <param name="parameters">
        /// The list of parameters found on the method, as they may be given to <see cref="JsonRpcRequest.TryGetTypedArguments(ReadOnlySpan{ParameterInfo}, Span{object})"/>.
        /// Note this list may omit some special parameters such as a trailing <see cref="CancellationToken"/>.
        /// </param>
        public JsonRpcMethodAttribute? GetJsonRpcMethodAttribute(string methodName, ReadOnlySpan<ParameterInfo> parameters)
        {
            Requires.NotNull(methodName, nameof(methodName));
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

            return null;
        }

        /// <summary>
        /// Starts listening to incoming messages.
        /// </summary>
        public void StartListening()
        {
            Verify.Operation(this.MessageHandler.CanRead, Resources.StreamMustBeReadable);
            Verify.Operation(this.readLinesTask == null, Resources.InvalidAfterListenHasStarted);
            Verify.NotDisposed(this);

            // We take a lock around this Task.Run and field assignment,
            // and also immediately within the invoked Task itself,
            // to guarantee that the assignment will complete BEFORE we actually read the first message.
            // See the StartListening_ShouldNotAllowIncomingMessageToRaceWithInvokeAsync test.
            lock (this.syncObject)
            {
                this.readLinesTask = Task.Run(this.ReadAndHandleRequestsAsync, this.DisconnectedToken);
            }
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument"><inheritdoc cref="InvokeAsync{TResult}(string, object?)" path="/param[@name='argument']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task InvokeAsync(string targetName, object? argument)
        {
            return this.InvokeAsync<object>(targetName, argument);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task InvokeAsync(string targetName, params object?[]? arguments)
        {
            return this.InvokeAsync<object>(targetName, arguments);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument">A single method argument, must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>.</param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task<TResult> InvokeAsync<TResult>(string targetName, object? argument)
        {
            var arguments = new object?[] { argument };

            return this.InvokeWithCancellationAsync<TResult>(targetName, arguments, CancellationToken.None);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task<TResult> InvokeAsync<TResult>(string targetName, params object?[]? arguments)
        {
            // If somebody calls InvokeInternal<T>(id, "method", null), the null is not passed as an item in the array.
            // Instead, the compiler thinks that the null is the array itself and it'll pass null directly.
            // To account for this case, we check for null below.
            arguments = arguments ?? new object?[] { null };

            return this.InvokeWithCancellationAsync<TResult>(targetName, arguments, CancellationToken.None);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public Task InvokeWithParameterObjectAsync(string targetName, object? argument = null, CancellationToken cancellationToken = default(CancellationToken))
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        {
            return this.InvokeWithParameterObjectAsync<object>(targetName, argument, cancellationToken);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
        /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argumentDeclaredTypes']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task InvokeWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        {
            return this.InvokeWithParameterObjectAsync<object>(targetName, argument, argumentDeclaredTypes, cancellationToken);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object? argument = null, CancellationToken cancellationToken = default(CancellationToken))
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        {
            return this.InvokeWithParameterObjectAsync<TResult>(targetName, argument, null, cancellationToken);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument">An object whose properties match the names of parameters on the target method. Must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>.</param>
        /// <param name="argumentDeclaredTypes">
        /// A dictionary of <see cref="Type"/> objects that describe how each entry in the <see cref="IReadOnlyDictionary{TKey, TValue}"/> provided in <paramref name="argument"/> is expected by the server to be typed.
        /// If specified, this must have exactly the same set of keys as <paramref name="argument"/> and contain no <c>null</c> values.
        /// </param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        {
            // If argument is null, this indicates that the method does not take any parameters.
            object?[]? argumentToPass = argument == null ? null : new object?[] { argument };
            return this.InvokeCoreAsync<TResult>(this.CreateNewRequestId(), targetName, argumentToPass, positionalArgumentDeclaredTypes: null, argumentDeclaredTypes, cancellationToken, isParameterObject: true);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object?>? arguments = null, CancellationToken cancellationToken = default(CancellationToken))
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        {
            return this.InvokeWithCancellationAsync<object>(targetName, arguments, cancellationToken);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeWithCancellationAsync{TResult}(string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, CancellationToken)" path="/param[@name='argumentDeclaredTypes']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type> argumentDeclaredTypes, CancellationToken cancellationToken)
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        {
            return this.InvokeWithCancellationAsync<object>(targetName, arguments, argumentDeclaredTypes, cancellationToken);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        public Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object?>? arguments = null, CancellationToken cancellationToken = default(CancellationToken))
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        {
            return this.InvokeCoreAsync<TResult>(this.CreateNewRequestId(), targetName, arguments, cancellationToken);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">Type of the method result.</typeparam>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='positionalArgumentDeclaredTypes']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <returns>A task that completes when the server method executes and returns the result.</returns>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
        public Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        {
            return this.InvokeCoreAsync<TResult>(this.CreateNewRequestId(), targetName, arguments, argumentDeclaredTypes, namedArgumentDeclaredTypes: null, cancellationToken, isParameterObject: false);
        }

#pragma warning disable CA1200 // Avoid using cref tags with a prefix
        /// <summary>
        /// Invokes a given method on a JSON-RPC server without waiting for its response.
        /// </summary>
        /// <remarks>
        /// Any error that happens on the server side is ignored.
        /// </remarks>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument">Method argument, must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>.</param>
        /// <returns>A task that completes when the notify request is sent to the channel to the server.</returns>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="targetName" /> is empty.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has already been disposed prior to this call.</exception>
        /// <exception cref="ConnectionLostException">Thrown when the connection is terminated (by either side) while the request is being transmitted.</exception>
        /// <exception cref="Exception">
        /// Any exception thrown by the <see cref="IJsonRpcMessageFormatter"/> (typically due to serialization failures).
        /// When using <see cref="JsonMessageFormatter"/> this should be <see cref="JsonSerializationException"/>.
        /// When using <see cref="MessagePackFormatter"/> this should be <see cref="T:MessagePack.MessagePackSerializationException"/>.
        /// </exception>
        public Task NotifyAsync(string targetName, object? argument)
#pragma warning restore CA1200 // Avoid using cref tags with a prefix
        {
            var arguments = new object?[] { argument };

            return this.InvokeCoreAsync<object>(RequestId.NotSpecified, targetName, arguments, CancellationToken.None);
        }

        /// <inheritdoc cref="NotifyAsync(string, object?[], IReadOnlyList{Type}?)"/>
        public Task NotifyAsync(string targetName, params object?[]? arguments) => this.NotifyAsync(targetName, arguments, null);

        /// <summary><inheritdoc cref="NotifyAsync(string, object?)" path="/summary"/></summary>
        /// <remarks>
        /// Any error that happens on the server side is ignored.
        /// </remarks>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeWithCancellationAsync{TResult}(string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, CancellationToken)" path="/param[@name='argumentDeclaredTypes']"/></param>
        /// <returns>A task that completes when the notify request is sent to the channel to the server.</returns>
        /// <inheritdoc cref="NotifyAsync(string, object?)" path="/exception"/>
        public Task NotifyAsync(string targetName, object?[]? arguments, IReadOnlyList<Type>? argumentDeclaredTypes)
        {
            return this.InvokeCoreAsync<object>(RequestId.NotSpecified, targetName, arguments, argumentDeclaredTypes, null, CancellationToken.None, isParameterObject: false);
        }

#pragma warning disable RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.
        /// <inheritdoc cref="NotifyWithParameterObjectAsync(string, object?, IReadOnlyDictionary{string, Type}?)"/>
        public Task NotifyWithParameterObjectAsync(string targetName, object? argument = null) => this.NotifyWithParameterObjectAsync(targetName, argument, null);
#pragma warning restore RS0027 // Public API with optional parameter(s) should have the most parameters amongst its public overloads.

        /// <summary><inheritdoc cref="NotifyAsync(string, object?)" path="/summary"/></summary>
        /// <remarks>
        /// Any error that happens on the server side is ignored.
        /// </remarks>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
        /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='namedArgumentDeclaredTypes']"/></param>
        /// <returns>A task that completes when the notification has been transmitted.</returns>
        /// <inheritdoc cref="NotifyAsync(string, object?)" path="/exception"/>
        public Task NotifyWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes)
        {
            // If argument is null, this indicates that the method does not take any parameters.
            object?[]? argumentToPass = argument == null ? null : new object?[] { argument };

            return this.InvokeCoreAsync<object>(RequestId.NotSpecified, targetName, argumentToPass, null, argumentDeclaredTypes, CancellationToken.None, isParameterObject: true);
        }

        void IJsonRpcTracingCallbacks.OnMessageSerialized(JsonRpcMessage message, object encodedMessage)
        {
            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceData(TraceEventType.Information, (int)TraceEvents.MessageSent, message);
            }

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
            {
                this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)TraceEvents.MessageSent, "Sent: {0}", encodedMessage);
            }
        }

        void IJsonRpcTracingCallbacks.OnMessageDeserialized(JsonRpcMessage message, object encodedMessage)
        {
            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceData(TraceEventType.Information, (int)TraceEvents.MessageReceived, message);
            }

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Verbose))
            {
                this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)TraceEvents.MessageReceived, "Received: {0}", encodedMessage);
            }
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
        /// Creates a marshallable proxy for a given object that may be sent over RPC such that the receiving side can invoke methods on the given object.
        /// </summary>
        /// <typeparam name="T">
        /// The interface type implemented by the <paramref name="marshaledObject"/> that defines the members to expose over RPC.
        /// </typeparam>
        /// <param name="marshaledObject">The object to be exposed over RPC.</param>
        /// <param name="options"><inheritdoc cref="RpcMarshaledContext{T}(T, JsonRpcTargetOptions)" path="/param[@name='options']"/></param>
        /// <returns>A lifetime controlling wrapper around a new proxy value.</returns>
        /// <remarks>
        /// <para>
        /// Use <see cref="MarshalLimitedArgument{T}(T)"/> for a simpler lifetime model when the object should only be marshaled within the scope of a single RPC call.
        /// </para>
        /// </remarks>
        internal static IRpcMarshaledContext<T> MarshalWithControlledLifetime<T>(T marshaledObject, JsonRpcTargetOptions options)
            where T : class
        {
            return new RpcMarshaledContext<T>(marshaledObject, options);
        }

        /// <inheritdoc cref="MarshalWithControlledLifetime{T}(T, JsonRpcTargetOptions)"/>
        /// <param name="interfaceType"><inheritdoc cref="MarshalWithControlledLifetime{T}(T, JsonRpcTargetOptions)" path="/typeparam"/></param>
        /// <param name="marshaledObject"><inheritdoc cref="MarshalWithControlledLifetime{T}(T, JsonRpcTargetOptions)" path="/param[@name='marshaledObject']"/></param>
        /// <param name="options"><inheritdoc cref="MarshalWithControlledLifetime{T}(T, JsonRpcTargetOptions)" path="/param[@name='options']"/></param>
        internal static IRpcMarshaledContext<object> MarshalWithControlledLifetime(Type interfaceType, object marshaledObject, JsonRpcTargetOptions options)
        {
            return (IRpcMarshaledContext<object>)MarshalWithControlledLifetimeOpenGenericMethodInfo.MakeGenericMethod(interfaceType).Invoke(null, new object?[] { marshaledObject, options });
        }

        /// <inheritdoc cref="MarshalWithControlledLifetime{T}(T, JsonRpcTargetOptions)"/>
        /// <returns>A proxy value that may be used within an RPC argument so the RPC server may call back into the <paramref name="marshaledObject"/> object on the RPC client.</returns>
        /// <remarks>
        /// <para>
        /// Use <see cref="MarshalWithControlledLifetime{T}(T, JsonRpcTargetOptions)"/> for greater control and flexibility around lifetime of the proxy.
        /// This is required when the value is returned from an RPC method or when it is used within an RPC argument and must outlive that RPC invocation.
        /// </para>
        /// </remarks>
        internal static T MarshalLimitedArgument<T>(T marshaledObject)
            where T : class
        {
            // This method is included in the spec, but hasn't been implemented yet and has no callers.
            // It is here to match the spec and to help give some clarity around the boundaries of the MarshalWithControlledLifetime method's responsibilities.
            throw new NotImplementedException();
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
        /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <c>false</c>.</exception>
        internal void AddLocalRpcMethod(MethodInfo handler, object? target, JsonRpcMethodAttribute? methodRpcSettings, SynchronizationContext? synchronizationContext)
        {
            Requires.NotNull(handler, nameof(handler));
            Requires.Argument(handler.IsStatic == (target == null), nameof(target), Resources.TargetObjectAndMethodStaticFlagMismatch);

            this.ThrowIfConfigurationLocked();
            string rpcMethodName = methodRpcSettings?.Name ?? handler.Name;
            lock (this.syncObject)
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

        /// <inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)"/>
        /// <returns>An object that may be disposed of to revert the addition of the target object. Will be null if and only if <paramref name="requestRevertOption"/> is <c>false</c>.</returns>
        internal IDisposable? AddLocalRpcTargetInternal(Type exposingMembersOn, object target, JsonRpcTargetOptions? options, bool requestRevertOption)
        {
            RevertAddLocalRpcTarget? revert = requestRevertOption ? new RevertAddLocalRpcTarget(this) : null;
            options = options ?? JsonRpcTargetOptions.Default;
            Dictionary<string, List<MethodSignatureAndTarget>> mapping = GetRequestMethodToClrMethodMap(exposingMembersOn.GetTypeInfo(), target, options);
            lock (this.syncObject)
            {
                foreach (KeyValuePair<string, List<MethodSignatureAndTarget>> item in mapping)
                {
                    string rpcMethodName = options.MethodNameTransform != null ? options.MethodNameTransform(item.Key) : item.Key;
                    Requires.Argument(rpcMethodName != null, nameof(options), nameof(JsonRpcTargetOptions.MethodNameTransform) + " delegate returned a value that is not a legal RPC method name.");
                    if (this.targetRequestMethodToClrMethodMap.TryGetValue(rpcMethodName, out List<MethodSignatureAndTarget>? existingList))
                    {
                        // Only add methods that do not have equivalent signatures to what we already have.
                        foreach (MethodSignatureAndTarget newMethod in item.Value)
                        {
                            if (!existingList.Any(e => e.Signature.Equals(newMethod.Signature)))
                            {
                                this.TraceLocalMethodAdded(rpcMethodName, newMethod);
                                revert?.RecordMethodAdded(rpcMethodName, newMethod);
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
                        foreach (MethodSignatureAndTarget newMethod in item.Value)
                        {
                            this.TraceLocalMethodAdded(rpcMethodName, newMethod);
                            revert?.RecordMethodAdded(rpcMethodName, newMethod);
                        }

                        this.targetRequestMethodToClrMethodMap.Add(rpcMethodName, item.Value);
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
                                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalEventListenerAdded, "Listening for events from {0}.{1} to raise notification.", target.GetType().FullName, evt.Name);
                                }

                                var eventReceiver = new EventReceiver(this, target, evt, options);
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
        /// <seealso cref="CreateExceptionFromRpcError(JsonRpcRequest, JsonRpcError)"/>
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
        /// Creates a <see cref="RemoteRpcException"/> (or derived type) that represents the data found in a JSON-RPC error response.
        /// This is called on the client side to produce the exception that will be thrown back to the RPC client.
        /// </summary>
        /// <param name="request">The JSON-RPC request that produced this error.</param>
        /// <param name="response">The JSON-RPC error response.</param>
        /// <returns>An instance of <see cref="RemoteRpcException"/>.</returns>
        /// <seealso cref="CreateErrorDetails(JsonRpcRequest, Exception)"/>
        protected virtual RemoteRpcException CreateExceptionFromRpcError(JsonRpcRequest request, JsonRpcError response)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(response, nameof(response));
            Assumes.NotNull(request.Method);

            Assumes.NotNull(response.Error);
            Type? dataType = this.GetErrorDetailsDataType(response);
            object? deserializedData = dataType != null ? response.Error.GetData(dataType) : response.Error.Data;
            switch (response.Error.Code)
            {
                case JsonRpcErrorCode.InvalidParams:
                case JsonRpcErrorCode.MethodNotFound:
                    return new RemoteMethodNotFoundException(response.Error.Message, request.Method, response.Error.Code, response.Error.Data, deserializedData);
                case JsonRpcErrorCode.ResponseSerializationFailure:
                    return new RemoteSerializationException(response.Error.Message, response.Error.Data, deserializedData);

                default:
                    return new RemoteInvocationException(response.Error.Message, (int)response.Error.Code, response.Error.Data, deserializedData);
            }
        }

        /// <summary>
        /// Determines the type that the <see cref="JsonRpcError.ErrorDetail.Data"/> object should be deserialized to
        /// for an incoming <see cref="JsonRpcError"/> message.
        /// </summary>
        /// <param name="error">The received error message.</param>
        /// <returns>
        /// The type, or <c>null</c> if the type is unknown.
        /// </returns>
        /// <remarks>
        /// The default implementation matches what <see cref="CreateErrorDetails(JsonRpcRequest, Exception)"/> does
        /// by assuming that the <see cref="JsonRpcError.ErrorDetail.Data"/> object should be deserialized as an instance of <see cref="CommonErrorData"/>.
        /// However derived types can override this method and use <see cref="JsonRpcError.ErrorDetail.Code"/> or other means to determine the appropriate type.
        /// </remarks>
#pragma warning disable CA1716 // Identifiers should not match keywords
        protected virtual Type? GetErrorDetailsDataType(JsonRpcError error) => typeof(CommonErrorData);
#pragma warning restore CA1716 // Identifiers should not match keywords

        /// <summary>
        /// Invokes the specified RPC method.
        /// </summary>
        /// <typeparam name="TResult">RPC method return type.</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        [Obsolete("Use the InvokeCoreAsync(RequestId, ...) overload instead.")]
        protected Task<TResult> InvokeCoreAsync<TResult>(long? id, string targetName, IReadOnlyList<object?>? arguments, CancellationToken cancellationToken)
        {
            return this.InvokeCoreAsync<TResult>(id.HasValue ? new RequestId(id.Value) : default, targetName, arguments, cancellationToken);
        }

        /// <summary>
        /// Invokes the specified RPC method.
        /// </summary>
        /// <typeparam name="TResult">RPC method return type.</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        protected Task<TResult> InvokeCoreAsync<TResult>(RequestId id, string targetName, IReadOnlyList<object?>? arguments, CancellationToken cancellationToken)
        {
            return this.InvokeCoreAsync<TResult>(id, targetName, arguments, cancellationToken, isParameterObject: false);
        }

        /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
        /// <typeparam name="TResult">RPC method return type.</typeparam>
        /// <param name="id">An identifier established by the Client. If the default value is given, it is assumed to be a notification.</param>
        /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
        /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
        /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
        /// <param name="isParameterObject">Value which indicates if parameter should be passed as an object.</param>
        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
        [Obsolete("Use the InvokeCoreAsync(RequestId, ...) overload instead.")]
#pragma warning disable CA1068 // CancellationToken parameters must come last
        protected Task<TResult> InvokeCoreAsync<TResult>(long? id, string targetName, IReadOnlyList<object?>? arguments, CancellationToken cancellationToken, bool isParameterObject)
#pragma warning restore CA1068 // CancellationToken parameters must come last
        {
            return this.InvokeCoreAsync<TResult>(id.HasValue ? new RequestId(id.Value) : default, targetName, arguments, cancellationToken, isParameterObject);
        }

        /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)"/>
#pragma warning disable CA1068 // CancellationToken parameters must come last
        protected Task<TResult> InvokeCoreAsync<TResult>(RequestId id, string targetName, IReadOnlyList<object?>? arguments, CancellationToken cancellationToken, bool isParameterObject)
#pragma warning restore CA1068 // CancellationToken parameters must come last
        {
            return this.InvokeCoreAsync<TResult>(id, targetName, arguments, null, null, cancellationToken, isParameterObject);
        }

#pragma warning disable CA1200 // Avoid using cref tags with a prefix
        /// <summary>
        /// Invokes a given method on a JSON-RPC server.
        /// </summary>
        /// <typeparam name="TResult">RPC method return type.</typeparam>
        /// <param name="id">An identifier established by the Client. If the default value is given, it is assumed to be a notification.</param>
        /// <param name="targetName">Name of the method to invoke. Must not be null or empty.</param>
        /// <param name="arguments">Arguments to pass to the invoked method. They must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>. If <c>null</c>, no arguments are passed.</param>
        /// <param name="positionalArgumentDeclaredTypes">
        /// A list of <see cref="Type"/> objects that describe how each element in <paramref name="arguments"/> is expected by the server to be typed.
        /// If specified, this must have exactly the same length as <paramref name="arguments"/> and contain no <c>null</c> elements.
        /// This value is ignored when <paramref name="isParameterObject"/> is true.
        /// </param>
        /// <param name="namedArgumentDeclaredTypes">
        /// A dictionary of <see cref="Type"/> objects that describe how each entry in the <see cref="IReadOnlyDictionary{TKey, TValue}"/> provided in the only element of <paramref name="arguments"/> is expected by the server to be typed.
        /// If specified, this must have exactly the same set of keys as the dictionary contained in the first element of <paramref name="arguments"/>, and contain no <c>null</c> values.
        /// </param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <param name="isParameterObject">Value which indicates if parameter should be passed as an object.</param>
        /// <returns>A task that completes with the response from the JSON-RPC server.</returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown after <paramref name="cancellationToken"/> is canceled.
        /// If the request has already been transmitted, the exception is only thrown after the server has received the cancellation notification and responded to it.
        /// If the server completes the request instead of cancelling, this exception will not be thrown.
        /// When the connection drops before receiving a response, this exception is thrown if <paramref name="cancellationToken"/> has been canceled.
        /// </exception>
        /// <exception cref="RemoteRpcException">
        /// A common base class for a variety of RPC exceptions that may be thrown. Some common derived types are listed individually.
        /// </exception>
        /// <exception cref="RemoteInvocationException">
        /// Thrown when an error is returned from the server in consequence of executing the requested method.
        /// </exception>
        /// <exception cref="RemoteMethodNotFoundException">
        /// Thrown when the server reports that no matching method was found to invoke.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="targetName" /> is empty.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has already been disposed prior to this call.</exception>
        /// <exception cref="ConnectionLostException">
        /// Thrown when the connection is terminated (by either side) while the request is in progress,
        /// unless <paramref name="cancellationToken"/> is already signaled.
        /// </exception>
        /// <exception cref="Exception">
        /// Any exception thrown by the <see cref="IJsonRpcMessageFormatter"/> (typically due to serialization failures).
        /// When using <see cref="JsonMessageFormatter"/> this should be <see cref="JsonSerializationException"/>.
        /// When using <see cref="MessagePackFormatter"/> this should be <see cref="T:MessagePack.MessagePackSerializationException"/>.
        /// </exception>
#pragma warning disable CA1068 // CancellationToken parameters must come last
        protected async Task<TResult> InvokeCoreAsync<TResult>(RequestId id, string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type>? positionalArgumentDeclaredTypes, IReadOnlyDictionary<string, Type>? namedArgumentDeclaredTypes, CancellationToken cancellationToken, bool isParameterObject)
#pragma warning restore CA1068 // CancellationToken parameters must come last
#pragma warning restore CA1200 // Avoid using cref tags with a prefix
        {
            Requires.NotNullOrEmpty(targetName, nameof(targetName));

            cancellationToken.ThrowIfCancellationRequested();
            Verify.NotDisposed(this);

            var request = new JsonRpcRequest
            {
                RequestId = id,
                Method = targetName,
            };
            if (isParameterObject)
            {
                object? argument = arguments;
                if (arguments != null)
                {
                    if (arguments.Count != 1 || arguments[0] == null || !arguments[0]!.GetType().GetTypeInfo().IsClass)
                    {
                        throw new ArgumentException(Resources.ParameterNotObject);
                    }

                    argument = arguments[0];
                }

                request.Arguments = argument;
                if (namedArgumentDeclaredTypes is object)
                {
                    Requires.Argument(namedArgumentDeclaredTypes.Count == request.ArgumentCount, nameof(namedArgumentDeclaredTypes), Resources.TypedArgumentsLengthMismatch);
                    request.NamedArgumentDeclaredTypes = namedArgumentDeclaredTypes;
                }
            }
            else
            {
                request.Arguments = arguments ?? Array.Empty<object>();

                if (positionalArgumentDeclaredTypes is object)
                {
                    Requires.Argument(positionalArgumentDeclaredTypes.Count == request.ArgumentCount, nameof(positionalArgumentDeclaredTypes), Resources.TypedArgumentsLengthMismatch);
                    request.ArgumentListDeclaredTypes = positionalArgumentDeclaredTypes;
                }
            }

            JsonRpcMessage? response = await this.InvokeCoreAsync(request, typeof(TResult), cancellationToken).ConfigureAwait(false);

            if (request.IsResponseExpected)
            {
                if (response is JsonRpcError error)
                {
                    throw this.CreateExceptionFromRpcError(request, error);
                }
                else if (response is JsonRpcResult result)
                {
                    return result.GetResult<TResult>();
                }
                else
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Resources.ResponseUnexpectedFormat, JsonConvert.SerializeObject(response)));
                }
            }
            else
            {
                return default!;
            }
        }

        /// <summary>
        /// Creates a unique <see cref="RequestId"/> for an outbound request.
        /// </summary>
        /// <returns>The unique <see cref="RequestId"/>.</returns>
        protected virtual RequestId CreateNewRequestId()
        {
            long id = Interlocked.Increment(ref this.nextId);
            return new RequestId(id);
        }

        /// <summary>
        /// Raises the <see cref="IJsonRpcFormatterCallbacks.RequestTransmissionAborted"/> event.
        /// </summary>
        /// <param name="request">The request whose transmission could not be completed.</param>
        protected virtual void OnRequestTransmissionAborted(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));
            if (!request.RequestId.IsEmpty)
            {
                this.requestTransmissionAborted?.Invoke(this, new JsonRpcMessageEventArgs(request));
            }
        }

        /// <summary>
        /// Raises the <see cref="IJsonRpcFormatterCallbacks.ResponseReceived"/> event.
        /// </summary>
        /// <param name="response">The result or error that was received.</param>
        protected virtual void OnResponseReceived(JsonRpcMessage response) => this.responseReceived?.Invoke(this, new JsonRpcResponseEventArgs((IJsonRpcMessageWithId)Requires.NotNull(response, nameof(response))));

        /// <summary>
        /// Raises the <see cref="IJsonRpcFormatterCallbacks.ResponseSent"/> event.
        /// </summary>
        /// <param name="response">The result or error that was sent.</param>
        protected virtual void OnResponseSent(JsonRpcMessage response) => this.responseSent?.Invoke(this, new JsonRpcResponseEventArgs((IJsonRpcMessageWithId)Requires.NotNull(response, nameof(response))));

        /// <summary>
        /// Creates a dictionary which maps a request method name to its clr method name via <see cref="JsonRpcMethodAttribute" /> value.
        /// </summary>
        /// <param name="exposedMembersOnType">Type to reflect over and analyze its methods.</param>
        /// <param name="target">The instance of <paramref name="exposedMembersOnType"/> to be exposed to RPC.</param>
        /// <param name="options">The options that apply for this target object.</param>
        /// <returns>Dictionary which maps a request method name to its clr method name.</returns>
        private static Dictionary<string, List<MethodSignatureAndTarget>> GetRequestMethodToClrMethodMap(TypeInfo exposedMembersOnType, object target, JsonRpcTargetOptions options)
        {
            Requires.NotNull(target, nameof(target));
            Requires.NotNull(exposedMembersOnType, nameof(exposedMembersOnType));
            Requires.NotNull(options, nameof(options));

            var clrMethodToRequestMethodMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestMethodToClrMethodNameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestMethodToDelegateMap = new Dictionary<string, List<MethodSignatureAndTarget>>(StringComparer.Ordinal);
            var candidateAliases = new Dictionary<string, string>(StringComparer.Ordinal);

            var mapping = new MethodNameMap(exposedMembersOnType);

            for (TypeInfo? t = exposedMembersOnType; t != null && t != typeof(object).GetTypeInfo(); t = t.BaseType?.GetTypeInfo())
            {
                // As we enumerate methods, skip accessor methods
                foreach (MethodInfo method in t.DeclaredMethods.Where(m => !m.IsSpecialName))
                {
                    if (!options.AllowNonPublicInvocation && !method.IsPublic && !exposedMembersOnType.IsInterface)
                    {
                        continue;
                    }

                    var requestName = mapping.GetRpcMethodName(method);

                    if (!requestMethodToDelegateMap.TryGetValue(requestName, out List<MethodSignatureAndTarget>? methodTargetList))
                    {
                        methodTargetList = new List<MethodSignatureAndTarget>();
                        requestMethodToDelegateMap.Add(requestName, methodTargetList);
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

                    if (attribute == null && options.UseSingleObjectParameterDeserialization)
                    {
                        attribute = new JsonRpcMethodAttribute(null) { UseSingleObjectParameterDeserialization = true };
                    }

                    // Skip this method if its signature matches one from a derived type we have already scanned.
                    MethodSignatureAndTarget methodTarget = new MethodSignatureAndTarget(method, target, attribute, perMethodSynchronizationContext: null);
                    if (methodTargetList.Contains(methodTarget))
                    {
                        continue;
                    }

                    methodTargetList.Add(methodTarget);

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

            return requestMethodToDelegateMap;
        }

        private static JsonRpcError CreateCancellationResponse(JsonRpcRequest request)
        {
            return new JsonRpcError
            {
                RequestId = request.RequestId,
                Error = new JsonRpcError.ErrorDetail
                {
                    Code = JsonRpcErrorCode.RequestCanceled,
                    Message = Resources.TaskWasCancelled,
                },
            };
        }

        private static Exception StripExceptionToInnerException(Exception exception)
        {
            if ((exception is TargetInvocationException || exception is AggregateException) && exception.InnerException is object)
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
        private static bool TryGetTaskOfTOrValueTaskOfTType(TypeInfo taskTypeInfo, [NotNullWhen(true)] out TypeInfo? taskOfTTypeInfo)
        {
            Requires.NotNull(taskTypeInfo, nameof(taskTypeInfo));

            // Make sure we're prepared for Task<T>-derived types, by walking back up to the actual type in order to find the Result property.
            TypeInfo? taskTypeInfoLocal = taskTypeInfo;
            while (taskTypeInfoLocal != null)
            {
                bool isTaskOfTOrValueTaskOfT = taskTypeInfoLocal.IsGenericType &&
                    (taskTypeInfoLocal.GetGenericTypeDefinition() == typeof(Task<>) || taskTypeInfoLocal.GetGenericTypeDefinition() == typeof(ValueTask<>));
                if (isTaskOfTOrValueTaskOfT)
                {
                    taskOfTTypeInfo = taskTypeInfoLocal;
                    return true;
                }

                taskTypeInfoLocal = taskTypeInfoLocal.BaseType?.GetTypeInfo();
            }

            taskOfTTypeInfo = null;
            return false;
        }

        /// <summary>
        /// Convert a <see cref="ValueTask"/> or <see cref="ValueTask{T}"/> into a <see cref="Task"/> if possible.
        /// </summary>
        /// <param name="result">The result from the RPC method invocation.</param>
        /// <param name="task">Receives the converted <see cref="Task"/> object, if conversion was possible; otherwise <c>null</c>.</param>
        /// <returns><c>true</c> if conversion succeeded; <c>false</c> otherwise.</returns>
        private static bool TryGetTaskFromValueTask(object? result, [NotNullWhen(true)] out Task? task)
        {
            if (result is ValueTask resultingValueTask)
            {
                task = resultingValueTask.AsTask();
                return true;
            }

            if (result != null)
            {
                TypeInfo resultTypeInfo = result.GetType().GetTypeInfo();
                if (resultTypeInfo.IsGenericType && resultTypeInfo.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    task = (Task)resultTypeInfo.GetDeclaredMethod(nameof(ValueTask<int>.AsTask))!.Invoke(result, Array.Empty<object>())!;
                    return true;
                }
            }

            task = null;
            return false;
        }

        private async Task<JsonRpcMessage?> InvokeCoreAsync(JsonRpcRequest request, Type? expectedResultType, CancellationToken cancellationToken)
        {
            Requires.NotNull(request, nameof(request));
            Assumes.NotNull(request.Method);

            try
            {
                using (CancellationTokenExtensions.CombinedCancellationToken cts = this.DisconnectedToken.CombineWith(cancellationToken))
                {
                    if (!request.IsResponseExpected)
                    {
                        if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Verbose, System.Diagnostics.Tracing.EventKeywords.None))
                        {
                            JsonRpcEventSource.Instance.SendingNotification(request.Method, JsonRpcEventSource.GetArgumentsString(request.Arguments));
                        }

                        await this.TransmitAsync(request, cts.Token).ConfigureAwait(false);
                        return default;
                    }

                    Verify.Operation(this.readLinesTask != null, Resources.InvalidBeforeListenHasStarted);
                    var tcs = new TaskCompletionSource<JsonRpcMessage>();
                    Action<JsonRpcMessage?> dispatcher = (response) =>
                    {
                        lock (this.dispatcherMapLock)
                        {
                            this.resultDispatcherMap.Remove(request.RequestId);
                        }

                        try
                        {
                            if (response == null)
                            {
                                if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                                {
                                    this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestAbandonedByRemote, "Aborting pending request \"{0}\" because the connection was lost.", request.RequestId);
                                }

                                if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
                                {
                                    JsonRpcEventSource.Instance.ReceivedNoResponse(request.RequestId.NumberIfPossibleForEvent);
                                }

                                if (cancellationToken.IsCancellationRequested)
                                {
                                    // Consider lost connection to be result of task canceled and set state to canceled.
                                    tcs.TrySetCanceled(cancellationToken);
                                }
                                else
                                {
                                    tcs.TrySetException(new ConnectionLostException());
                                }
                            }
                            else if (response is JsonRpcError error)
                            {
                                if (error.Error != null && JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
                                {
                                    JsonRpcEventSource.Instance.ReceivedError(request.RequestId.NumberIfPossibleForEvent, error.Error.Code);
                                }

                                if (error.Error?.Code == JsonRpcErrorCode.RequestCanceled)
                                {
                                    tcs.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
                                }
                                else
                                {
                                    tcs.SetResult(response);
                                }
                            }
                            else
                            {
                                if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
                                {
                                    JsonRpcEventSource.Instance.ReceivedResult(request.RequestId.NumberIfPossibleForEvent);
                                }

                                tcs.SetResult(response);
                            }
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                        {
                            tcs.TrySetException(ex);
                        }
                    };

                    var callData = new OutstandingCallData(tcs, dispatcher, expectedResultType);
                    lock (this.dispatcherMapLock)
                    {
                        this.resultDispatcherMap.Add(request.RequestId, callData);
                    }

                    try
                    {
                        if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Verbose, System.Diagnostics.Tracing.EventKeywords.None))
                        {
                            JsonRpcEventSource.Instance.SendingRequest(request.RequestId.NumberIfPossibleForEvent, request.Method, JsonRpcEventSource.GetArgumentsString(request.Arguments));
                        }

                        await this.TransmitAsync(request, cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Since we aren't expecting a response to this request, clear out our memory of it to avoid a memory leak.
                        lock (this.dispatcherMapLock)
                        {
                            this.resultDispatcherMap.Remove(request.RequestId);
                        }

                        throw;
                    }

                    // Arrange for sending a cancellation message if canceled while we're waiting for a response.
                    try
                    {
                        using (cancellationToken.Register(this.cancelPendingOutboundRequestAction, request.RequestId, useSynchronizationContext: false))
                        {
                            // This task will be completed when the Response object comes back from the other end of the pipe
                            return await tcs.Task.ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            this.CancellationStrategy?.OutboundRequestEnded(request.RequestId);
                        }
                    }
                }
            }
            catch (OperationCanceledException ex) when (this.DisconnectedToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectionLostException(Resources.ConnectionDropped, ex);
            }
        }

        private JsonRpcError CreateErrorForResponseTransmissionFailure(JsonRpcRequest request, Exception exception)
        {
            JsonRpcError.ErrorDetail errorDetails = this.CreateErrorDetails(request, exception);
            this.ThrowIfNullDetail(exception, errorDetails);

            errorDetails.Code = JsonRpcErrorCode.ResponseSerializationFailure;
            return new JsonRpcError
            {
                RequestId = request.RequestId,
                Error = errorDetails,
            };
        }

        private JsonRpcError CreateError(JsonRpcRequest request, Exception exception)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(exception, nameof(exception));

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Error))
            {
                this.TraceSource.TraceEvent(TraceEventType.Error, (int)TraceEvents.LocalInvocationError, "Exception thrown from request \"{0}\" for method {1}: {2}", request.RequestId, request.Method, exception);
                this.TraceSource.TraceData(TraceEventType.Error, (int)TraceEvents.LocalInvocationError, exception, request.Method, request.RequestId, request.Arguments);
            }

            exception = StripExceptionToInnerException(exception);

            JsonRpcError.ErrorDetail errorDetails = this.CreateErrorDetails(request, exception);
            this.ThrowIfNullDetail(exception, errorDetails);

            return new JsonRpcError
            {
                RequestId = request.RequestId,
                Error = errorDetails,
            };
        }

        private void ThrowIfNullDetail(Exception exception, JsonRpcError.ErrorDetail errorDetails)
        {
            if (errorDetails is null)
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
        }

        private async ValueTask<JsonRpcMessage> DispatchIncomingRequestAsync(JsonRpcRequest request)
        {
            Requires.NotNull(request, nameof(request));
            Requires.Argument(request.Method != null, nameof(request), "Method must be set.");

            CancellationTokenSource? localMethodCancellationSource = null;
            CancellationTokenRegistration disconnectedRegistration = default;
            try
            {
                // Add cancelation to inboundCancellationSources before yielding to ensure that
                // it cannot be preempted by the cancellation request that would try to set it
                // Fix for https://github.com/Microsoft/vs-streamjsonrpc/issues/56
                CancellationToken cancellationToken = CancellationToken.None;
                if (request.IsResponseExpected)
                {
#pragma warning disable CA2000 // Dispose objects before losing scope
                    localMethodCancellationSource = new CancellationTokenSource();
#pragma warning restore CA2000 // Dispose objects before losing scope
                    cancellationToken = localMethodCancellationSource.Token;

                    if (this.CancelLocallyInvokedMethodsWhenConnectionIsClosed)
                    {
                        // We do NOT use CancellationTokenSource.CreateLinkedToken because that link is unbreakable,
                        // and we need to break the link (but without disposing the CTS we created) at the conclusion of this method.
                        // Disposing the CTS causes .NET Framework (in its default configuration) to throw when CancellationToken.Register is called later,
                        // which causes problems with some long-lived server methods such as those that return IAsyncEnumerable<T>.
                        disconnectedRegistration = this.DisconnectedToken.Register(state => ((CancellationTokenSource)state).Cancel(), localMethodCancellationSource);
                    }
                }

                TargetMethod? targetMethod = null;
                lock (this.syncObject)
                {
                    if (this.targetRequestMethodToClrMethodMap.TryGetValue(request.Method, out List<MethodSignatureAndTarget>? candidateTargets))
                    {
                        targetMethod = new TargetMethod(request, candidateTargets, this.SynchronizationContextOrDefault);
                    }
                }

                if (targetMethod != null && targetMethod.IsFound)
                {
                    // Add cancelation to inboundCancellationSources before yielding to ensure that
                    // it cannot be preempted by the cancellation request that would try to set it
                    // Fix for https://github.com/Microsoft/vs-streamjsonrpc/issues/56
                    if (targetMethod.AcceptsCancellationToken && request.IsResponseExpected)
                    {
                        this.CancellationStrategy?.IncomingRequestStarted(request.RequestId, localMethodCancellationSource!);
                    }

                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                    {
                        this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalInvocation, "Invoking {0}", targetMethod);
                    }

                    if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Verbose, System.Diagnostics.Tracing.EventKeywords.None))
                    {
                        if (request.IsResponseExpected)
                        {
                            JsonRpcEventSource.Instance.ReceivedRequest(request.RequestId.NumberIfPossibleForEvent, request.Method, JsonRpcEventSource.GetArgumentsString(request.Arguments));
                        }
                        else
                        {
                            JsonRpcEventSource.Instance.ReceivedNotification(request.Method, JsonRpcEventSource.GetArgumentsString(request.Arguments));
                        }
                    }

                    object? result;
                    try
                    {
                        // IMPORTANT: This should be the first await in this async method,
                        //            and no other await should be between this one and actually invoking the target method.
                        //            This is crucial to the guarantee that method invocation order is preserved from client to server
                        //            when a single-threaded SynchronizationContext is applied.
                        result = await targetMethod.InvokeAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
                    {
                        return CreateCancellationResponse(request);
                    }

                    // Convert ValueTask to Task or ValueTask<T> to Task<T>
                    if (TryGetTaskFromValueTask(result, out Task? resultTask))
                    {
                        result = resultTask;
                    }

                    if (!(result is Task resultingTask))
                    {
                        if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
                        {
                            JsonRpcEventSource.Instance.SendingResult(request.RequestId.NumberIfPossibleForEvent);
                        }

                        try
                        {
                            await this.ProcessResultBeforeSerializingAsync(result, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return CreateCancellationResponse(request);
                        }

                        return new JsonRpcResult
                        {
                            RequestId = request.RequestId,
                            Result = result,
                            ResultDeclaredType = targetMethod.ReturnType,
                        };
                    }

                    // Avoid another first chance exception from re-throwing here. We'll handle faults in our HandleInvocationTask* methods below.
                    await resultingTask.NoThrowAwaitable(false);

                    // Pick continuation delegate based on whether a Task.Result exists based on method declaration.
                    // Checking on the runtime result object itself is problematic because .NET / C# implements
                    // async Task methods to return a Task<VoidTaskResult> instance, and we shouldn't consider
                    // the VoidTaskResult internal struct as a meaningful result.
                    return TryGetTaskOfTOrValueTaskOfTType(targetMethod.ReturnType!.GetTypeInfo(), out _)
                        ? await this.HandleInvocationTaskOfTResultAsync(request, resultingTask, cancellationToken).ConfigureAwait(false)
                        : this.HandleInvocationTaskResult(request, resultingTask);
                }
                else
                {
                    ImmutableList<JsonRpc> remoteRpcTargets = this.remoteRpcTargets;

                    // If we can't find the method or the target object does not exist or does not contain methods, we relay the message to the server.
                    // Any exceptions from the relay will be returned back to the origin since we catch all exceptions here.  The message being relayed to the
                    // server would share the same id as the message sent from origin. We just take the message objec wholesale and pass it along to the
                    // other side.
                    if (!remoteRpcTargets.IsEmpty)
                    {
                        if (request.IsResponseExpected)
                        {
                            this.CancellationStrategy?.IncomingRequestStarted(request.RequestId, localMethodCancellationSource!);
                        }

                        // Yield now so method invocation is async and we can proceed to handle other requests meanwhile.
                        // IMPORTANT: This should be the first await in this async method,
                        //            and no other await should be between this one and actually invoking the target method.
                        //            This is crucial to the guarantee that method invocation order is preserved from client to server
                        //            when a single-threaded SynchronizationContext is applied.
                        await this.SynchronizationContextOrDefault;

                        // Before we forward the request to the remote targets, we need to change the request ID so it gets a new ID in case we run into collisions.  For example,
                        // if origin issues a request destined for the remote target at the same time as a request issued by the relay to the remote target, their IDs could be mixed up.
                        // See InvokeRemoteTargetWithExistingId unit test for an example.
                        RequestId previousId = request.RequestId;

                        JsonRpcMessage? remoteResponse = null;
                        foreach (JsonRpc remoteTarget in remoteRpcTargets)
                        {
                            if (request.IsResponseExpected)
                            {
                                request.RequestId = remoteTarget.CreateNewRequestId();
                            }

                            CancellationToken token = request.IsResponseExpected ? localMethodCancellationSource!.Token : CancellationToken.None;
                            remoteResponse = await remoteTarget.InvokeCoreAsync(request, null, token).ConfigureAwait(false);

                            if (remoteResponse is JsonRpcError error && error.Error != null)
                            {
                                if (error.Error.Code == JsonRpcErrorCode.MethodNotFound || error.Error.Code == JsonRpcErrorCode.InvalidParams)
                                {
                                    // If the result is an error and that error is method not found or invalid parameters on the remote target, then we continue on to the next target.
                                    continue;
                                }
                            }

                            // Otherwise, we simply return the json response;
                            break;
                        }

                        if (remoteResponse != null)
                        {
                            if (remoteResponse is IJsonRpcMessageWithId messageWithId)
                            {
                                messageWithId.RequestId = previousId;
                            }

                            return remoteResponse;
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
                            RequestId = request.RequestId,
                            Error = new JsonRpcError.ErrorDetail
                            {
                                Code = JsonRpcErrorCode.MethodNotFound,
                                Message = string.Format(CultureInfo.CurrentCulture, Resources.RpcMethodNameNotFound, request.Method),
                            },
                        };
                    }
                    else
                    {
                        if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestWithoutMatchingTarget, "Invocation of \"{0}\" cannot occur because arguments do not match any registered target methods.", request.Method);
                        }

                        return new JsonRpcError
                        {
                            RequestId = request.RequestId,
                            Error = new JsonRpcError.ErrorDetail
                            {
                                Code = JsonRpcErrorCode.InvalidParams,
                                Message = targetMethod.LookupErrorMessage,
                                Data = targetMethod.ArgumentDeserializationFailures is object ? new CommonErrorData(targetMethod.ArgumentDeserializationFailures) : null,
                            },
                        };
                    }
                }
            }
            catch (Exception ex) when (!this.IsFatalException(StripExceptionToInnerException(ex)))
            {
                JsonRpcError error = this.CreateError(request, ex);

                if (error.Error != null && JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
                {
                    JsonRpcEventSource.Instance.SendingError(request.RequestId.NumberIfPossibleForEvent, error.Error.Code);
                }

                return error;
            }
            finally
            {
                if (localMethodCancellationSource != null)
                {
                    this.CancellationStrategy?.IncomingRequestEnded(request.RequestId);

                    // Be sure to dispose the link to the local method token we created in case it is linked to our long-lived disposal token
                    // and otherwise cause a memory leak.
                    disconnectedRegistration.Dispose();
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

            JsonRpcMessage result;
            if (t.IsFaulted)
            {
                Exception exception = StripExceptionToInnerException(t.Exception!);
                if (this.IsFatalException(exception))
                {
                    var e = new JsonRpcDisconnectedEventArgs(
                        string.Format(CultureInfo.CurrentCulture, Resources.FatalExceptionWasThrown, exception.GetType(), exception.Message),
                        DisconnectedReason.FatalException,
                        exception);

                    this.OnJsonRpcDisconnected(e);
                }

                result = this.CreateError(request, t.Exception!);
            }
            else if (t.IsCanceled)
            {
                result = CreateCancellationResponse(request);
            }
            else
            {
                result = new JsonRpcResult
                {
                    RequestId = request.RequestId,
                };
            }

            if (result is JsonRpcError error)
            {
                if (error.Error != null && JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
                {
                    JsonRpcEventSource.Instance.SendingError(request.RequestId.NumberIfPossibleForEvent, error.Error.Code);
                }
            }
            else
            {
                if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
                {
                    JsonRpcEventSource.Instance.SendingResult(request.RequestId.NumberIfPossibleForEvent);
                }
            }

            return result;
        }

        private async ValueTask<JsonRpcMessage> HandleInvocationTaskOfTResultAsync(JsonRpcRequest request, Task t, CancellationToken cancellationToken)
        {
            // This method should only be called for methods that declare to return Task<T> (or a derived type), or ValueTask<T>.
            Assumes.True(TryGetTaskOfTOrValueTaskOfTType(t.GetType().GetTypeInfo(), out TypeInfo? taskOfTTypeInfo));

            object? result = null;
            Type? declaredResultType = null;
            if (t.Status == TaskStatus.RanToCompletion)
            {
                // If t is a Task<SomeType>, it will have Result property.
                // If t is just a Task, there is no Result property on it.
                // We can't really write direct code to deal with Task<T>, since we have no idea of T in this context, so we simply use reflection to
                // read the result at runtime.
#pragma warning disable VSTHRD002 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
#pragma warning disable VSTHRD102 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
                const string ResultPropertyName = nameof(Task<int>.Result);
#pragma warning restore VSTHRD002
#pragma warning restore VSTHRD102

                PropertyInfo? resultProperty = taskOfTTypeInfo.GetDeclaredProperty(ResultPropertyName);
                Assumes.NotNull(resultProperty);
                declaredResultType = resultProperty.PropertyType;
                result = resultProperty.GetValue(t);

                // Transfer the ultimate success/failure result of the operation from the original successful method to the post-processing step.
                t = this.ProcessResultBeforeSerializingAsync(result, cancellationToken);
                await t.NoThrowAwaitable(false);
            }

            JsonRpcMessage message = this.HandleInvocationTaskResult(request, t);
            if (message is JsonRpcResult resultMessage)
            {
                resultMessage.Result = result;
                resultMessage.ResultDeclaredType = declaredResultType;
            }

            return message;
        }

        /// <summary>
        /// Perform any special processing on the result of an RPC method before serializing it for transmission to the RPC client.
        /// </summary>
        /// <param name="result">The result from the RPC method.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that completes when processing the result is complete. The returned Task *may* transition to a <see cref="TaskStatus.Faulted"/> state.</returns>
#pragma warning disable CA1822 // Mark members as static
        private Task ProcessResultBeforeSerializingAsync(object? result, CancellationToken cancellationToken)
#pragma warning restore CA1822 // Mark members as static
        {
            // If result is a prefetching IAsyncEnumerable<T>, prefetch now.
            return JsonRpcExtensions.PrefetchIfApplicableAsync(result, cancellationToken);
        }

        private void OnJsonRpcDisconnected(JsonRpcDisconnectedEventArgs eventArgs)
        {
            EventHandler<JsonRpcDisconnectedEventArgs>? handlersToInvoke = null;
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

            try
            {
                TraceEventType eventType = (eventArgs.Reason == DisconnectedReason.LocallyDisposed || eventArgs.Reason == DisconnectedReason.RemotePartyTerminated)
                    ? TraceEventType.Information
                    : TraceEventType.Critical;
                if (this.TraceSource.Switch.ShouldTrace(eventType))
                {
                    this.TraceSource.TraceEvent(eventType, (int)TraceEvents.Closed, "Connection closing ({0}: {1}). {2}", eventArgs.Reason, eventArgs.Description, eventArgs.Exception);
                }

                // Fire the event first so that subscribers can interact with a non-disposed stream
                handlersToInvoke?.Invoke(this, eventArgs);
            }
            finally
            {
                // Dispose the stream and fault pending requests in the finally block
                // So this is executed even if Disconnected event handler throws.
                this.disconnectedSource.Cancel();

                this.JsonRpcDisconnectedShutdownAsync(eventArgs).Forget();
            }
        }

        private async Task JsonRpcDisconnectedShutdownAsync(JsonRpcDisconnectedEventArgs eventArgs)
        {
            Task messageHandlerDisposal = Task.CompletedTask;
            if (this.MessageHandler is Microsoft.VisualStudio.Threading.IAsyncDisposable asyncDisposableMessageHandler)
            {
                messageHandlerDisposal = asyncDisposableMessageHandler.DisposeAsync();
            }
            else if (this.MessageHandler is IDisposable disposableMessageHandler)
            {
                disposableMessageHandler.Dispose();
            }

            this.FaultPendingRequests();

            var exceptions = new List<Exception>();
            if (eventArgs.Exception is object)
            {
                exceptions.Add(eventArgs.Exception);
            }

            if (this.localTargetObjectsToDispose is object)
            {
                foreach (object target in this.localTargetObjectsToDispose)
                {
                    // We're calling Dispose on the target objects, so switch to the user-supplied SyncContext for those target objects.
                    await this.SynchronizationContextOrDefault;

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
                        exceptions.Add(ex);
                    }
                }

                await TaskScheduler.Default;
            }

            // Ensure the Task we may have returned from Completion is completed,
            // but not before any asynchronous disposal of our message handler completes.
            try
            {
                await messageHandlerDisposal.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                exceptions.Add(ex);
            }

            if (exceptions.Count > 0)
            {
                this.completionSource.TrySetException(exceptions.Count > 1 ? new AggregateException(exceptions) : exceptions[0]);
            }
            else
            {
                this.completionSource.TrySetResult(true);
            }
        }

        private void UnregisterEventHandlersFromTargetObjects()
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

        private async Task ReadAndHandleRequestsAsync()
        {
            lock (this.syncObject)
            {
                // This block intentionally left blank.
                // It ensures that this thread will not receive messages before our caller (StartListening)
                // assigns the Task we return to a field before we go any further,
                // since our caller holds this lock until the field assignment completes.
                // See the StartListening_ShouldNotAllowIncomingMessageToRaceWithInvokeAsync test.
            }

            try
            {
                this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.ListeningStarted, "Listening started.");

                while (!this.IsDisposed && !this.DisconnectedToken.IsCancellationRequested)
                {
                    JsonRpcMessage? protocolMessage = null;
                    try
                    {
                        protocolMessage = await this.MessageHandler.ReadAsync(this.DisconnectedToken).ConfigureAwait(false);
                        if (protocolMessage == null)
                        {
                            this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(Resources.ReachedEndOfStream, DisconnectedReason.RemotePartyTerminated));
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception exception)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(
                            string.Format(CultureInfo.CurrentCulture, Resources.ReadingJsonRpcStreamFailed, exception.GetType().Name, exception.Message),
                            exception is JsonException ? DisconnectedReason.ParseError : DisconnectedReason.StreamError,
                            exception));
                        return;
                    }

                    this.HandleRpcAsync(protocolMessage).Forget(); // all exceptions are handled internally

                    // We must clear buffers before reading the next message.
                    // HandleRpcAsync must do whatever deserialization it requires before it yields.
                    (this.MessageHandler as IJsonRpcMessageBufferManager)?.DeserializationComplete(protocolMessage);
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
            OutstandingCallData? data = null;
            try
            {
                if (rpc is JsonRpcRequest request)
                {
                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                    {
                        if (request.IsResponseExpected)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.RequestReceived, "Received request \"{0}\" for method \"{1}\".", request.RequestId, request.Method);
                        }
                        else
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.RequestReceived, "Received notification for method \"{0}\".", request.Method);
                        }
                    }

                    // We can't accept a request that requires a response if we can't write.
                    Verify.Operation(!request.IsResponseExpected || this.MessageHandler.CanWrite, Resources.StreamMustBeWriteable);

                    JsonRpcMessage result;
                    lock (this.syncObject)
                    {
                        if (this.requestsInDispatchCount++ == 0)
                        {
                            this.dispatchCompletionSource.Reset();
                        }
                    }

                    try
                    {
                        result = await this.DispatchIncomingRequestAsync(request).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (this.syncObject)
                        {
                            if (--this.requestsInDispatchCount == 0)
                            {
                                this.dispatchCompletionSource.Set();
                            }
                        }
                    }

                    if (request.IsResponseExpected && !this.IsDisposed)
                    {
                        bool responseSent = false;
                        try
                        {
                            await this.TransmitAsync(result, this.DisconnectedToken).ConfigureAwait(false);
                            responseSent = true;
                        }
                        catch (OperationCanceledException) when (this.DisconnectedToken.IsCancellationRequested)
                        {
                        }
                        catch (ObjectDisposedException) when (this.IsDisposed)
                        {
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception exception)
#pragma warning restore CA1031 // Do not catch general exception types
                        {
                            // Some exceptions are fatal. If we aren't already disconnected, try sending an apology to the client.
                            if (!this.DisconnectedToken.IsCancellationRequested)
                            {
                                result = this.CreateErrorForResponseTransmissionFailure(request, exception);
                                await this.TransmitAsync(result, this.DisconnectedToken).ConfigureAwait(false);
                                responseSent = true;
                            }
                        }

                        if (responseSent)
                        {
                            this.OnResponseSent(result);
                        }
                    }
                }
                else if (rpc is IJsonRpcMessageWithId resultOrError)
                {
                    this.OnResponseReceived(rpc);
                    JsonRpcResult? result = resultOrError as JsonRpcResult;
                    JsonRpcError? error = resultOrError as JsonRpcError;

                    lock (this.dispatcherMapLock)
                    {
                        if (this.resultDispatcherMap.TryGetValue(resultOrError.RequestId, out data))
                        {
                            this.resultDispatcherMap.Remove(resultOrError.RequestId);
                        }
                    }

                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                    {
                        if (result != null)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.ReceivedResult, "Received result for request \"{0}\".", result.RequestId);
                        }
                        else if (error?.Error is object)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.ReceivedError, "Received error response for request {0}: {1} \"{2}\": ", error.RequestId, error.Error.Code, error.Error.Message);
                        }
                    }

                    if (data is object)
                    {
                        if (data.ExpectedResultType != null && rpc is JsonRpcResult resultMessage)
                        {
                            resultMessage.SetExpectedResultType(data.ExpectedResultType);
                        }
                        else if (rpc is JsonRpcError errorMessage && errorMessage.Error != null)
                        {
                            Type? errorType = this.GetErrorDetailsDataType(errorMessage);
                            if (errorType != null)
                            {
                                errorMessage.Error.SetExpectedDataType(errorType);
                            }
                        }

                        // Complete the caller's request with the response asynchronously so it doesn't delay handling of other JsonRpc messages.
                        await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                        data.CompletionHandler(rpc);
                        data = null; // avoid invoking again if we throw later
                    }
                    else
                    {
                        // Unexpected "response" to no request we have a record of. Raise disconnected event.
                        this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(
                            Resources.UnexpectedResponseWithNoMatchingRequest,
                            DisconnectedReason.RemoteProtocolViolation));
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
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                var eventArgs = new JsonRpcDisconnectedEventArgs(
                    string.Format(CultureInfo.CurrentCulture, Resources.UnexpectedErrorProcessingJsonRpc, ex.Message),
                    DisconnectedReason.ParseError,
                    ex);

                // Fatal error. Raise disconnected event.
                this.OnJsonRpcDisconnected(eventArgs);

                // If we extracted this callback from the collection already, take care to complete it to avoid hanging our client.
                data?.CompletionHandler(null);
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
        /// <param name="state">The <see cref="RequestId"/> associated with the request to be canceled.</param>
        private void CancelPendingOutboundRequest(object state)
        {
            Requires.NotNull(state, nameof(state));
            var requestId = (RequestId)state;
            this.CancellationStrategy?.CancelOutboundRequest(requestId);
        }

        private void TraceLocalMethodAdded(string rpcMethodName, MethodSignatureAndTarget targetMethod)
        {
            Requires.NotNullOrEmpty(rpcMethodName, nameof(rpcMethodName));

            if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
            {
                this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.LocalMethodAdded, "Added local RPC method \"{0}\" -> {1}", rpcMethodName, targetMethod);
            }
        }

        private async ValueTask TransmitAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            try
            {
                bool etwEnabled = JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None);
                if (etwEnabled)
                {
                    JsonRpcEventSource.Instance.TransmissionQueued();
                }

                await this.MessageHandler.WriteAsync(message, cancellationToken).ConfigureAwait(false);

                if (etwEnabled)
                {
                    JsonRpcEventSource.Instance.TransmissionCompleted();
                }
            }
            catch (Exception exception)
            {
                if ((this.MessageHandler as IDisposableObservable)?.IsDisposed ?? false)
                {
                    var e = new JsonRpcDisconnectedEventArgs(
                        string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcMessage, exception.GetType().Name, exception.Message),
                        DisconnectedReason.StreamError,
                        exception);

                    // Fatal error. Raise disconnected event.
                    this.OnJsonRpcDisconnected(e);
                }

                if (exception is OperationCanceledException)
                {
                    this.TraceSource.TraceEvent(TraceEventType.Information, (int)TraceEvents.TransmissionFailed, "Message transmission was canceled.");
                }
                else
                {
                    this.TraceSource.TraceEvent(TraceEventType.Error, (int)TraceEvents.TransmissionFailed, "Exception thrown while transmitting message: {0}", exception);
                }

                if (message is JsonRpcRequest request)
                {
                    this.OnRequestTransmissionAborted(request);
                }

                throw;
            }
        }

        /// <summary>
        /// Throws an exception if we have already started listening,
        /// unless <see cref="AllowModificationWhileListening"/> is <c>true</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if <see cref="HasListeningStarted"/> is <c>true</c> and <see cref="AllowModificationWhileListening"/> is <c>false</c>.</exception>
        private void ThrowIfConfigurationLocked()
        {
            Verify.Operation(!this.HasListeningStarted || this.AllowModificationWhileListening, Resources.MustNotBeListening);
        }

        internal class MethodNameMap
        {
            private readonly List<InterfaceMapping> interfaceMaps;

            internal MethodNameMap(TypeInfo typeInfo)
            {
                Requires.NotNull(typeInfo, nameof(typeInfo));
                this.interfaceMaps = typeInfo.IsInterface ? new List<InterfaceMapping>()
                    : typeInfo.ImplementedInterfaces.Select(typeInfo.GetInterfaceMap).ToList();
            }

            internal string GetRpcMethodName(MethodInfo method)
            {
                Requires.NotNull(method, nameof(method));

                return this.FindAttribute(method)?.Name ?? method.Name;
            }

            internal JsonRpcMethodAttribute? FindAttribute(MethodInfo method)
            {
                Requires.NotNull(method, nameof(method));

                // Get the custom attribute, which may appear on the method itself or the interface definition of the method where applicable.
                var attribute = (JsonRpcMethodAttribute?)method.GetCustomAttribute(typeof(JsonRpcMethodAttribute));
                if (attribute == null)
                {
                    attribute = (JsonRpcMethodAttribute?)this.FindMethodOnInterface(method)?.GetCustomAttribute(typeof(JsonRpcMethodAttribute));
                }

                return attribute;
            }

            private MethodInfo? FindMethodOnInterface(MethodInfo methodImpl)
            {
                Requires.NotNull(methodImpl, nameof(methodImpl));

                foreach (InterfaceMapping map in this.interfaceMaps)
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
            internal OutstandingCallData(object taskCompletionSource, Action<JsonRpcMessage?> completionHandler, Type? expectedResultType)
            {
                this.TaskCompletionSource = taskCompletionSource;
                this.CompletionHandler = completionHandler;
                this.ExpectedResultType = expectedResultType;
            }

            internal object TaskCompletionSource { get; }

            internal Action<JsonRpcMessage?> CompletionHandler { get; }

            internal Type? ExpectedResultType { get; }
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

        /// <summary>
        /// A class whose disposal will revert certain effects of a prior call to <see cref="AddLocalRpcTargetInternal(Type, object, JsonRpcTargetOptions?, bool)"/>.
        /// </summary>
        private class RevertAddLocalRpcTarget : IDisposable
        {
            private readonly JsonRpc jsonRpc;
            private object? objectToDispose;
            private List<(string RpcMethodName, MethodSignatureAndTarget Method)>? targetMethods;
            private List<EventReceiver>? eventReceivers;

            internal RevertAddLocalRpcTarget(JsonRpc jsonRpc)
            {
                this.jsonRpc = jsonRpc;
            }

            public void Dispose()
            {
                lock (this.jsonRpc.syncObject)
                {
                    if (this.objectToDispose is object)
                    {
                        this.jsonRpc.localTargetObjectsToDispose?.Remove(this.objectToDispose);
                    }

                    if (this.targetMethods is object)
                    {
                        foreach ((string RpcMethodName, MethodSignatureAndTarget Method) targetMethod in this.targetMethods)
                        {
                            if (this.jsonRpc.targetRequestMethodToClrMethodMap.TryGetValue(targetMethod.RpcMethodName, out List<MethodSignatureAndTarget>? list))
                            {
                                list.Remove(targetMethod.Method);
                            }
                        }
                    }

                    if (this.eventReceivers is object && this.jsonRpc.eventReceivers is object)
                    {
                        foreach (EventReceiver eventReceiver in this.eventReceivers)
                        {
                            this.jsonRpc.eventReceivers.Remove(eventReceiver);
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
    }
}
