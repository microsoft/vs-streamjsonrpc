// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Manages a JSON-RPC connection with another entity over a <see cref="Stream"/>.
/// </summary>
[DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
public class JsonRpc : IDisposableObservable, IJsonRpcFormatterCallbacks, IJsonRpcTracingCallbacks, ExceptionSerializationHelpers.IExceptionTypeLoader
{
    /// <summary>
    /// The <see cref="System.Threading.SynchronizationContext"/> to use to schedule work on the threadpool.
    /// </summary>
    internal static readonly SynchronizationContext DefaultSynchronizationContext = new SynchronizationContext();

    /// <summary>
    /// The name of the top-level field that we add to JSON-RPC messages to track JoinableTask context to mitigate deadlocks.
    /// </summary>
    private const string JoinableTaskTokenHeaderName = "joinableTaskToken";

    /// <summary>
    /// A singleton error object that can be returned by <see cref="DispatchIncomingRequestAsync(JsonRpcRequest)"/> in error cases
    /// for requests that are actually notifications and thus the error will be dropped.
    /// </summary>
    private static readonly JsonRpcError DroppedError = new();

    private static readonly LoadableTypeCollection DefaultRuntimeDeserializableTypes = default(LoadableTypeCollection)
        .Add(typeof(Exception))
        .Add(typeof(ArgumentException))
        .Add(typeof(InvalidOperationException));

#if NET
    private static readonly MethodInfo ValueTaskAsTaskMethodInfo = typeof(ValueTask<>).GetMethod(nameof(ValueTask<int>.AsTask))!;
    private static readonly MethodInfo ValueTaskGetResultMethodInfo = typeof(ValueTask<>).GetMethod("get_Result")!;
    private static readonly MethodInfo TaskGetResultMethodInfo = typeof(Task<>).GetMethod("get_Result")!;
#endif

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
    /// The source for the <see cref="DisconnectedToken"/> property.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly CancellationTokenSource disconnectedSource = new CancellationTokenSource();

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
    /// Tracks RPC target objects.
    /// </summary>
    private readonly RpcTargetInfo rpcTargetInfo;

    private ExceptionSerializationHelpers.IExceptionTypeLoader? trimUnsafeTypeLoader;

    private LoadableTypeCollection loadableTypes = DefaultRuntimeDeserializableTypes;

    /// <summary>
    /// List of remote RPC targets to call if connection should be relayed.
    /// </summary>
    private ImmutableList<JsonRpc> remoteRpcTargets = ImmutableList<JsonRpc>.Empty;

    private Task? readLinesTask;
    private long nextId = 1;
    private int requestsInDispatchCount;
    private JsonRpcDisconnectedEventArgs? disconnectedEventArgs;

    /// <summary>
    /// Backing field for the <see cref="TraceSource"/> property.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private TraceSource traceSource = new TraceSource(nameof(JsonRpc), SourceLevels.ActivityTracing | SourceLevels.Warning);

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
    /// Backing field for the <see cref="JoinableTaskFactory"/> property.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private JoinableTaskFactory? joinableTaskFactory;

    /// <summary>
    /// Backing field for the <see cref="JoinableTaskTokenTracker"/> property.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private JoinableTaskTokenTracker joinableTaskTracker = JoinableTaskTokenTracker.Default;

    /// <summary>
    /// Backing field for the <see cref="CancellationStrategy"/> property.
    /// </summary>
    private ICancellationStrategy? cancellationStrategy;

    /// <summary>
    /// Backing field for the <see cref="ActivityTracingStrategy"/> property.
    /// </summary>
    private IActivityTracingStrategy? activityTracingStrategy;

    /// <summary>
    /// Backing field for <see cref="ExceptionStrategy"/>.
    /// </summary>
    private ExceptionProcessing exceptionStrategy;

    /// <summary>
    /// Backing field for <see cref="ExceptionOptions"/>.
    /// </summary>
    private ExceptionSettings exceptionSettings = ExceptionSettings.UntrustedData;

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
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    public JsonRpc(Stream stream)
        : this(new HeaderDelimitedMessageHandler(Requires.NotNull(stream, nameof(stream)), stream, new JsonMessageFormatter()))
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
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    public JsonRpc(Stream? sendingStream, Stream? receivingStream, object? target = null)
        : this(new HeaderDelimitedMessageHandler(sendingStream, receivingStream, new JsonMessageFormatter()))
    {
        if (target is not null)
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
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    [RequiresUnreferencedCode(RuntimeReasons.UntypedRpcTarget)]
    public JsonRpc(IJsonRpcMessageHandler messageHandler, object? target)
        : this(messageHandler)
    {
        if (target is not null)
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

        this.rpcTargetInfo = new RpcTargetInfo(this);

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
                if (disconnectedArgs is null)
                {
                    this.DisconnectedPrivate += value;
                }
            }

            if (disconnectedArgs is not null)
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

        /// <summary>
        /// An instance of an <see cref="Exception"/>-derived type was serialized as its base type because it did not have the <see cref="SerializableAttribute"/> applied.
        /// </summary>
        ExceptionNotSerializable,

        /// <summary>
        /// An <see cref="Exception"/>-derived type could not be deserialized because it was missing a deserializing constructor.
        /// A base-type that <em>does</em> offer the constructor will be instantiated instead.
        /// </summary>
        ExceptionNotDeserializable,

        /// <summary>
        /// An error occurred while deserializing a value within an <see cref="IFormatterConverter"/> interface.
        /// </summary>
        IFormatterConverterDeserializationFailure,
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
    /// Gets or sets the <see cref="JoinableTaskFactory"/> to participate in to mitigate deadlocks with the main thread.
    /// </summary>
    /// <value>Defaults to null.</value>
    public JoinableTaskFactory? JoinableTaskFactory
    {
        get => this.joinableTaskFactory;

        set
        {
            this.ThrowIfConfigurationLocked();
            this.joinableTaskFactory = value;
        }
    }

    /// <summary>
    /// Gets or sets the <see cref="JoinableTaskTokenTracker"/> to use to correlate <see cref="JoinableTask"/> tokens.
    /// This property is only applicable when <see cref="JoinableTaskFactory"/> is <see langword="null" />.
    /// </summary>
    /// <value>Defaults to an instance shared with all other <see cref="JsonRpc"/> instances that do not otherwise set this value explicitly.</value>
    /// <remarks>
    /// <para>
    /// This property is ignored when <see cref="JoinableTaskFactory"/> is set to a non-<see langword="null" /> value.
    /// </para>
    /// <para>
    /// This property should only be set explicitly when in an advanced scenario where one process has many <see cref="JsonRpc"/> instances
    /// that interact with multiple remote processes such that avoiding correlating <see cref="JoinableTask"/> tokens across <see cref="JsonRpc"/> instances
    /// is undesirable.
    /// </para>
    /// </remarks>
    public JoinableTaskTokenTracker JoinableTaskTracker
    {
        get => this.joinableTaskTracker;

        set
        {
            Requires.NotNull(value, nameof(value));
            this.ThrowIfConfigurationLocked();
            this.joinableTaskTracker = value;
        }
    }

    /// <summary>
    /// Gets a human-readable name that can be used to identify this <see cref="JsonRpc"/> instance.
    /// </summary>
    /// <remarks>
    /// This value is not used for any RPC functionality. It's merely an aid to help backtrace this instance to its creator.
    /// </remarks>
    public string? DisplayName { get; init; }

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
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            return this.completionSource.Task;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
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
    /// <value>The default is <see langword="false"/>.</value>
    /// <remarks>
    /// By default, all configuration such as target objects and target methods must be set
    /// before listening starts to avoid a race condition whereby we receive a method invocation
    /// message before we have wired up a handler for it and must reject the call.
    /// But in some advanced scenarios, it may be necessary to add target methods after listening
    /// has started (e.g. in response to an invocation that enables additional functionality),
    /// in which case setting this property to <see langword="true"/> is appropriate.
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
    /// Gets or sets a value indicating whether exceptions thrown by the RPC server should be fully serialized
    /// for the RPC client to then deserialize.
    /// </summary>
    /// <value>The default value is <see cref="ExceptionProcessing.CommonErrorData"/>.</value>
    /// <remarks>
    /// This setting influences the implementations of error processing virtual methods on this class.
    /// When those methods are overridden by a derived type, this property may have different or no impact on behavior.
    /// This does not alter how <see cref="LocalRpcException"/> behaves when thrown, since that exception type supplies all the details of the error response directly.
    /// </remarks>
    public ExceptionProcessing ExceptionStrategy
    {
        get => this.exceptionStrategy;
        set
        {
            this.ThrowIfConfigurationLocked();
            this.exceptionStrategy = value;
        }
    }

    /// <summary>
    /// Gets or sets the settings to use for serializing/deserializing exceptions.
    /// </summary>
    public ExceptionSettings ExceptionOptions
    {
        get => this.exceptionSettings;
        set
        {
            Requires.NotNull(value, nameof(value));
            this.ThrowIfConfigurationLocked();
            this.exceptionSettings = value;
        }
    }

    /// <summary>
    /// Gets or sets the strategy for propagating activity IDs over RPC.
    /// </summary>
    public IActivityTracingStrategy? ActivityTracingStrategy
    {
        get => this.activityTracingStrategy;
        set
        {
            this.ThrowIfConfigurationLocked();
            this.activityTracingStrategy = value;
        }
    }

    /// <summary>
    /// Gets the set of types that can be deserialized by name at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This set of types is used by the default implementation of <see cref="LoadTypeTrimSafe(string, string?)"/> to determine
    /// which types can be deserialized when their name is encountered in an RPC message.
    /// At present, it is only used for deserializing <see cref="Exception"/>-derived types.
    /// </para>
    /// <para>
    /// The default collection includes <see cref="Exception" />, <see cref="InvalidOperationException"/> and <see cref="ArgumentException"/>.
    /// </para>
    /// <para>
    /// As a `ref return` property, this property may be set to a collection of loadable types intended for sharing across <see cref="JsonRpc" /> instances.
    /// </para>
    /// </remarks>
    public ref LoadableTypeCollection LoadableTypes => ref this.loadableTypes;

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
    internal SynchronizationContext SynchronizationContextOrDefault => this.SynchronizationContext ?? DefaultSynchronizationContext;

    /// <summary>
    /// Gets a trim unsafe type loader.
    /// </summary>
    /// <remarks>
    /// A trim-safe loader is implemented by <see cref="JsonRpc"/> itself.
    /// </remarks>
    internal ExceptionSerializationHelpers.IExceptionTypeLoader TrimUnsafeTypeLoader
    {
        [RequiresUnreferencedCode(RuntimeReasons.LoadType)]
        get => this.trimUnsafeTypeLoader ??= new NotTrimSafeTypeLoader(this);
    }

    /// <summary>
    /// Gets a value indicating whether listening has started.
    /// </summary>
    private bool HasListeningStarted => this.readLinesTask is not null;

    private string DebuggerDisplay => $"JsonRpc: {this.DisplayName ?? "(anonymous)"}";

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
    /// <see cref="HeaderDelimitedMessageHandler"/> around messages serialized using the
    /// <see cref="JsonMessageFormatter"/>, and immediately starts listening.
    /// </summary>
    /// <param name="stream">A bidirectional stream to send and receive RPC messages on.</param>
    /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
    /// <returns>The initialized and listening <see cref="JsonRpc"/> object.</returns>
#pragma warning disable RS0027 // API with optional parameter(s) should have the most parameters amongst its public overloads
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    public static JsonRpc Attach(Stream stream, object? target = null)
#pragma warning restore RS0027 // API with optional parameter(s) should have the most parameters amongst its public overloads
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
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    public static JsonRpc Attach(Stream? sendingStream, Stream? receivingStream, object? target = null)
    {
        if (sendingStream is null && receivingStream is null)
        {
            throw new ArgumentException(Resources.BothReadableWritableAreNull);
        }

        var rpc = new JsonRpc(sendingStream, receivingStream, target);
        try
        {
            if (receivingStream is not null)
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
    /// <remarks>
    /// Calls to this method are intercepted by a source generator and replaced with a NativeAOT-compatible method call
    /// <see href="https://microsoft.github.io/vs-streamjsonrpc/docs/nativeAOT.html">when the interceptor is enabled</see>.
    /// </remarks>
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
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
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    public static T Attach<T>(Stream? sendingStream, Stream? receivingStream)
        where T : class
    {
        var rpc = new JsonRpc(sendingStream, receivingStream);
        T proxy = (T)rpc.CreateProxy(new ProxyInputs { ContractInterface = typeof(T) });
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
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
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
    /// <param name="options">A set of customizations for how the client proxy is wired up. If <see langword="null"/>, default options will be used.</param>
    /// <returns>
    /// An instance of the generated proxy.
    /// In addition to implementing <typeparamref name="T"/>, it also implements <see cref="IDisposable"/>
    /// and should be disposed of to close the connection.
    /// </returns>
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    public static T Attach<T>(IJsonRpcMessageHandler handler, JsonRpcProxyOptions? options)
        where T : class
    {
        var rpc = new JsonRpc(handler);
        T proxy = (T)rpc.CreateProxy(new ProxyInputs { ContractInterface = typeof(T), Options = options });
        rpc.StartListening();
        return proxy;
    }

    /// <summary>
    /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
    /// </summary>
    /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
    /// <returns>An instance of the generated proxy.</returns>
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    public T Attach<T>()
        where T : class
    {
        return this.Attach<T>(null);
    }

    /// <summary>
    /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
    /// </summary>
    /// <typeparam name="T">The interface that describes the functions available on the remote end.</typeparam>
    /// <param name="options">A set of customizations for how the client proxy is wired up. If <see langword="null"/>, default options will be used.</param>
    /// <returns>An instance of the generated proxy.</returns>
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    public T Attach<T>(JsonRpcProxyOptions? options)
        where T : class
    {
        return (T)this.CreateProxy(new ProxyInputs { ContractInterface = typeof(T), Options = options });
    }

    /// <summary>
    /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
    /// </summary>
    /// <param name="interfaceType">The interface that describes the functions available on the remote end.</param>
    /// <returns>An instance of the generated proxy.</returns>
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    public object Attach(Type interfaceType) => this.Attach(interfaceType, options: null);

    /// <summary>
    /// Creates a JSON-RPC client proxy that conforms to the specified server interface.
    /// </summary>
    /// <param name="interfaceType">The interface that describes the functions available on the remote end.</param>
    /// <param name="options">A set of customizations for how the client proxy is wired up. If <see langword="null"/>, default options will be used.</param>
    /// <returns>An instance of the generated proxy.</returns>
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    public object Attach(Type interfaceType, JsonRpcProxyOptions? options)
    {
        Requires.NotNull(interfaceType, nameof(interfaceType));
        return this.CreateProxy(new ProxyInputs { ContractInterface = interfaceType.GetTypeInfo(), Options = options });
    }

    /// <summary>
    /// Creates a JSON-RPC client proxy that conforms to the specified server interfaces.
    /// </summary>
    /// <param name="interfaceTypes">The interfaces that describes the functions available on the remote end.</param>
    /// <param name="options">A set of customizations for how the client proxy is wired up. If <see langword="null"/>, default options will be used.</param>
    /// <returns>An instance of the generated proxy.</returns>
    /// <inheritdoc cref="Attach{T}(Stream)" path="/remarks"/>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    public object Attach(ReadOnlySpan<Type> interfaceTypes, JsonRpcProxyOptions? options)
    {
        Requires.Argument(interfaceTypes.Length > 0, nameof(interfaceTypes), Resources.RequiredArgumentMissing);
        return this.CreateProxy(new ProxyInputs { ContractInterface = interfaceTypes[0], AdditionalContractInterfaces = interfaceTypes[1..].ToArray(), Options = options });
    }

    /// <inheritdoc cref="AddLocalRpcTarget(object, JsonRpcTargetOptions?)"/>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    [RequiresUnreferencedCode(RuntimeReasons.UntypedRpcTarget)]
    public void AddLocalRpcTarget(object target) => this.AddLocalRpcTarget(target, null);

    /// <inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)"/>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    [RequiresUnreferencedCode(RuntimeReasons.UntypedRpcTarget)]
    public void AddLocalRpcTarget(object target, JsonRpcTargetOptions? options) => this.AddLocalRpcTarget(Requires.NotNull(target, nameof(target)).GetType(), target, options);

    /// <inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)"/>
    /// <typeparam name="T"><inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)" path="/param[@name='exposingMembersOn']"/></typeparam>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    public void AddLocalRpcTarget<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T target, JsonRpcTargetOptions? options)
        where T : notnull => this.AddLocalRpcTarget(typeof(T), target, options);

    /// <inheritdoc cref="RpcTargetInfo.AddLocalRpcTarget(RpcTargetMetadata, object, JsonRpcTargetOptions?, bool)"/>
    /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <see langword="false"/>.</exception>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    public void AddLocalRpcTarget(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type exposingMembersOn,
        object target,
        JsonRpcTargetOptions? options)
    {
        Requires.NotNull(exposingMembersOn, nameof(exposingMembersOn));
        Requires.NotNull(target, nameof(target));
        this.ThrowIfConfigurationLocked();

        this.AddLocalRpcTargetInternal(exposingMembersOn, target, options, requestRevertOption: false);
    }

    /// <inheritdoc cref="AddLocalRpcTarget(Type, object, JsonRpcTargetOptions?)"/>
    public void AddLocalRpcTarget(RpcTargetMetadata exposingMembersOn, object target, JsonRpcTargetOptions? options)
    {
        Requires.NotNull(exposingMembersOn);
        Requires.NotNull(target);
        this.ThrowIfConfigurationLocked();

        options ??= JsonRpcTargetOptions.Default;
        this.rpcTargetInfo.AddLocalRpcTarget(exposingMembersOn, target, options, requestRevertOption: false);
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
    /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <see langword="false"/>.</exception>
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
    /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <see langword="false"/>.</exception>
    public void AddLocalRpcMethod(string? rpcMethodName, MethodInfo handler, object? target) => this.AddLocalRpcMethod(handler, target, new JsonRpcMethodAttribute(rpcMethodName));

    /// <inheritdoc cref="AddLocalRpcMethod(MethodInfo, object?, JsonRpcMethodAttribute?, SynchronizationContext?)"/>
    public void AddLocalRpcMethod(MethodInfo handler, object? target, JsonRpcMethodAttribute? methodRpcSettings) => this.AddLocalRpcMethod(handler, target, methodRpcSettings, synchronizationContext: null);

    /// <inheritdoc cref="RpcTargetInfo.GetJsonRpcMethodAttribute(string, ReadOnlySpan{ParameterInfo})"/>
    public JsonRpcMethodAttribute? GetJsonRpcMethodAttribute(string methodName, ReadOnlySpan<ParameterInfo> parameters)
    {
        Requires.NotNull(methodName, nameof(methodName));
        return this.rpcTargetInfo.GetJsonRpcMethodAttribute(methodName, parameters);
    }

    /// <summary>
    /// Starts listening to incoming messages.
    /// </summary>
    public void StartListening()
    {
        Verify.Operation(this.MessageHandler.CanRead, Resources.StreamMustBeReadable);
        Verify.Operation(this.readLinesTask is null, Resources.InvalidAfterListenHasStarted);
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
    [RequiresUnreferencedCode("The untyped 'argument' may be reflected over. Use the NamedArgs overload instead for trim safety.")]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "OverloadResolutionPriority resolves it.")]
    public Task InvokeWithParameterObjectAsync(string targetName, object? argument = null, CancellationToken cancellationToken = default(CancellationToken))
    {
        return this.InvokeWithParameterObjectAsync<object>(targetName, argument, cancellationToken);
    }

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument">An object that captures the parameter names, types, and the arguments to be passed to the RPC method.</param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    [OverloadResolutionPriority(10)]
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "OverloadResolutionPriority resolves it.")]
    public Task InvokeWithParameterObjectAsync(string targetName, NamedArgs? argument = null, CancellationToken cancellationToken = default(CancellationToken))
        => this.InvokeWithParameterObjectAsync(targetName, argument, argument?.DeclaredArgumentTypes, cancellationToken);

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
    /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argumentDeclaredTypes']"/></param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    [RequiresUnreferencedCode("The untyped 'argument' may be reflected over. Use the NamedArgs overload instead for trim safety.")]
    public Task InvokeWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        => this.InvokeWithParameterObjectAsync<object>(targetName, argument, argumentDeclaredTypes, cancellationToken);

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
    /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argumentDeclaredTypes']"/></param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    public Task InvokeWithParameterObjectAsync(string targetName, IReadOnlyDictionary<string, object?>? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
    {
        // If argument is null, this indicates that the method does not take any parameters.
        object?[]? argumentToPass = argument is null ? null : [argument];
        return this.InvokeCoreAsync<object>(this.CreateNewRequestId(), targetName, argumentToPass, positionalArgumentDeclaredTypes: null, argumentDeclaredTypes, cancellationToken, isParameterObject: true);
    }

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <typeparam name="TResult">Type of the method result.</typeparam>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    [RequiresUnreferencedCode("The untyped 'argument' may be reflected over. Use the NamedArgs overload instead for trim safety.")]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "OverloadResolutionPriority resolves it.")]
    public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object? argument = null, CancellationToken cancellationToken = default(CancellationToken))
    {
        return this.InvokeWithParameterObjectAsync<TResult>(targetName, argument, (argument as NamedArgs)?.DeclaredArgumentTypes, cancellationToken);
    }

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <typeparam name="TResult">Type of the method result.</typeparam>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync(string, NamedArgs?, CancellationToken)" path="/param[@name='argument']"/></param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    [OverloadResolutionPriority(10)]
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "OverloadResolutionPriority resolves it.")]
    public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, NamedArgs? argument = null, CancellationToken cancellationToken = default(CancellationToken))
    {
        // If argument is null, this indicates that the method does not take any parameters.
        object?[]? argumentToPass = argument is null ? null : new object?[] { argument };
        return this.InvokeCoreAsync<TResult>(this.CreateNewRequestId(), targetName, argumentToPass, positionalArgumentDeclaredTypes: null, argument?.DeclaredArgumentTypes, cancellationToken, isParameterObject: true);
    }

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <typeparam name="TResult">Type of the method result.</typeparam>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument">An object whose properties match the names of parameters on the target method. Must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>.</param>
    /// <param name="argumentDeclaredTypes">
    /// A dictionary of <see cref="Type"/> objects that describe how each entry in the <see cref="IReadOnlyDictionary{TKey, TValue}"/> provided in <paramref name="argument"/> is expected by the server to be typed.
    /// If specified, this must have exactly the same set of keys as <paramref name="argument"/> and contain no <see langword="null"/> values.
    /// </param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    [RequiresUnreferencedCode("The untyped 'argument' may be reflected over. Use the NamedArgs overload instead for trim safety.")]
    public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
    {
        // If argument is null, this indicates that the method does not take any parameters.
        object?[]? argumentToPass = argument is null ? null : new object?[] { argument };
        return this.InvokeCoreAsync<TResult>(this.CreateNewRequestId(), targetName, argumentToPass, positionalArgumentDeclaredTypes: null, argumentDeclaredTypes, cancellationToken, isParameterObject: true);
    }

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <typeparam name="TResult">Type of the method result.</typeparam>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument">An object whose properties match the names of parameters on the target method. Must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>.</param>
    /// <param name="argumentDeclaredTypes">
    /// A dictionary of <see cref="Type"/> objects that describe how each entry in the <see cref="IReadOnlyDictionary{TKey, TValue}"/> provided in <paramref name="argument"/> is expected by the server to be typed.
    /// If specified, this must have exactly the same set of keys as <paramref name="argument"/> and contain no <see langword="null"/> values.
    /// </param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, IReadOnlyDictionary<string, object?>? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
    {
        // If argument is null, this indicates that the method does not take any parameters.
        object?[]? argumentToPass = argument is null ? null : [argument];
        return this.InvokeCoreAsync<TResult>(this.CreateNewRequestId(), targetName, argumentToPass, positionalArgumentDeclaredTypes: null, argumentDeclaredTypes, cancellationToken, isParameterObject: true);
    }

    /// <summary><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/summary"/></summary>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="arguments"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='arguments']"/></param>
    /// <param name="cancellationToken"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='cancellationToken']"/></param>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/returns"/>
    /// <inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/exception"/>
    public Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object?>? arguments = null, CancellationToken cancellationToken = default(CancellationToken))
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
    public Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type> argumentDeclaredTypes, CancellationToken cancellationToken)
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
    public Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object?>? arguments = null, CancellationToken cancellationToken = default(CancellationToken))
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

    /// <inheritdoc cref="NotifyWithParameterObjectAsync(string, object?, IReadOnlyDictionary{string, Type}?)"/>
    [RequiresUnreferencedCode("The untyped 'argument' may be reflected over. Use the NamedArgs overload instead for trim safety.")]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "OverloadResolutionPriority resolves it.")]
    public Task NotifyWithParameterObjectAsync(string targetName, object? argument = null) => this.NotifyWithParameterObjectAsync(targetName, argument, (argument as NamedArgs)?.DeclaredArgumentTypes);

    /// <inheritdoc cref="NotifyWithParameterObjectAsync(string, object?, IReadOnlyDictionary{string, Type}?)"/>
    [OverloadResolutionPriority(10)]
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "OverloadResolutionPriority resolves it.")]
    public Task NotifyWithParameterObjectAsync(string targetName, NamedArgs? argument = null) => this.NotifyWithParameterObjectAsync(targetName, argument, argument?.DeclaredArgumentTypes);

    /// <summary><inheritdoc cref="NotifyAsync(string, object?)" path="/summary"/></summary>
    /// <remarks>
    /// Any error that happens on the server side is ignored.
    /// </remarks>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="argument"><inheritdoc cref="InvokeWithParameterObjectAsync{TResult}(string, object?, IReadOnlyDictionary{string, Type}?, CancellationToken)" path="/param[@name='argument']"/></param>
    /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='namedArgumentDeclaredTypes']"/></param>
    /// <returns>A task that completes when the notification has been transmitted.</returns>
    /// <inheritdoc cref="NotifyAsync(string, object?)" path="/exception"/>
    [RequiresUnreferencedCode("The untyped 'argument' may be reflected over. Use the NamedArgs overload instead for trim safety.")]
    public Task NotifyWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes)
    {
        // If argument is null, this indicates that the method does not take any parameters.
        object?[]? argumentToPass = argument is null ? null : [argument];

        return this.InvokeCoreAsync<object>(RequestId.NotSpecified, targetName, argumentToPass, null, argumentDeclaredTypes, CancellationToken.None, isParameterObject: true);
    }

    /// <summary><inheritdoc cref="NotifyAsync(string, object?)" path="/summary"/></summary>
    /// <remarks>
    /// Any error that happens on the server side is ignored.
    /// </remarks>
    /// <param name="targetName"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='targetName']"/></param>
    /// <param name="namedArguments">A dictionary of parameter names and arguments.</param>
    /// <param name="argumentDeclaredTypes"><inheritdoc cref="InvokeCoreAsync{TResult}(RequestId, string, IReadOnlyList{object?}?, IReadOnlyList{Type}?, IReadOnlyDictionary{string, Type}?, CancellationToken, bool)" path="/param[@name='namedArgumentDeclaredTypes']"/></param>
    /// <returns>A task that completes when the notification has been transmitted.</returns>
    /// <inheritdoc cref="NotifyAsync(string, object?)" path="/exception"/>
    public Task NotifyWithParameterObjectAsync(string targetName, IReadOnlyDictionary<string, object?>? namedArguments, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes)
    {
        // If argument is null, this indicates that the method does not take any parameters.
        object?[]? argumentToPass = namedArguments is null ? null : [namedArguments];
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

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
    Type? ExceptionSerializationHelpers.IExceptionTypeLoader.Load(string typeFullName, string? assemblyName) => this.LoadTypeTrimSafe(typeFullName, assemblyName);

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
    /// <param name="interfaceType"><inheritdoc cref="RpcMarshaledContext(Type, object, JsonRpcTargetOptions)" path="/param[@name='interfaceType']"/></param>
    /// <param name="marshaledObject"><inheritdoc cref="RpcMarshaledContext(Type, object, JsonRpcTargetOptions)" path="/param[@name='marshaledObject']"/></param>
    /// <param name="options"><inheritdoc cref="RpcMarshaledContext(Type, object, JsonRpcTargetOptions)" path="/param[@name='options']"/></param>
    /// <returns>A lifetime controlling wrapper around a new proxy value.</returns>
    /// <remarks>
    /// <para>
    /// Use <see cref="MarshalLimitedArgument{T}(T)"/> for a simpler lifetime model when the object should only be marshaled within the scope of a single RPC call.
    /// </para>
    /// </remarks>
    internal static RpcMarshaledContext MarshalWithControlledLifetime(Type interfaceType, object marshaledObject, JsonRpcTargetOptions options)
        => new RpcMarshaledContext(interfaceType, marshaledObject, options);

    /// <inheritdoc cref="MarshalWithControlledLifetime(Type, object, JsonRpcTargetOptions)"/>
    /// <returns>A proxy value that may be used within an RPC argument so the RPC server may call back into the <paramref name="marshaledObject"/> object on the RPC client.</returns>
    /// <remarks>
    /// <para>
    /// Use <see cref="MarshalWithControlledLifetime(Type, object, JsonRpcTargetOptions)"/> for greater control and flexibility around lifetime of the proxy.
    /// This is required when the value is returned from an RPC method or when it is used within an RPC argument and must outlive that RPC invocation.
    /// </para>
    /// </remarks>
    internal static T MarshalLimitedArgument<T>(T marshaledObject)
        where T : class
    {
        // This method is included in the spec, but hasn't been implemented yet and has no callers.
        // It is here to match the spec and to help give some clarity around the boundaries of the MarshalWithControlledLifetimeOpen method's responsibilities.
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates a JSON-RPC client proxy that implements a given set of interfaces.
    /// </summary>
    /// <param name="proxyInputs">Parameters for the proxy.</param>
    /// <returns>An instance of the generated proxy.</returns>
    [RequiresDynamicCode(RuntimeReasons.RefEmit), RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    internal IJsonRpcClientProxyInternal CreateProxy(in ProxyInputs proxyInputs)
    {
        if (proxyInputs.Options?.ProxySource is not JsonRpcProxyOptions.ProxyImplementation.AlwaysDynamic && ProxyBase.TryCreateProxy(this, proxyInputs, out IJsonRpcClientProxy? proxy))
        {
            return (IJsonRpcClientProxyInternal)proxy;
        }

#if !NETSTANDARD2_0
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException("CreateProxy is not supported if dynamic code is not supported.");
        }
#endif

        if (proxyInputs.Options?.ProxySource is JsonRpcProxyOptions.ProxyImplementation.AlwaysSourceGenerated)
        {
            throw proxyInputs.CreateNoSourceGeneratedProxyException();
        }

        TypeInfo proxyType = ProxyGeneration.Get(proxyInputs);
        return (IJsonRpcClientProxyInternal)Activator.CreateInstance(
            proxyType.AsType(),
            this,
            proxyInputs.Options ?? JsonRpcProxyOptions.Default,
            proxyInputs.MarshaledObjectHandle,
            proxyInputs.Options?.OnDispose)!;
    }

    /// <inheritdoc cref="RpcTargetInfo.AddLocalRpcMethod(MethodInfo, object?, JsonRpcMethodAttribute?, SynchronizationContext?)"/>
    /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartListening"/> is called and <see cref="AllowModificationWhileListening"/> is <see langword="false"/>.</exception>
    internal void AddLocalRpcMethod(MethodInfo handler, object? target, JsonRpcMethodAttribute? methodRpcSettings, SynchronizationContext? synchronizationContext)
    {
        this.ThrowIfConfigurationLocked();
        this.rpcTargetInfo.AddLocalRpcMethod(handler, target, methodRpcSettings, synchronizationContext);
    }

    /// <summary>
    /// Adds the specified target as possible object to invoke when incoming messages are received.
    /// </summary>
    /// <param name="exposingMembersOn">
    /// The type whose members define the RPC accessible members of the <paramref name="target"/> object.
    /// If this type is not an interface, only public members become invokable unless <see cref="JsonRpcTargetOptions.AllowNonPublicInvocation"/> is set to true on the <paramref name="options"/> argument.
    /// </param>
    /// <param name="target">Target to invoke when incoming messages are received.</param>
    /// <param name="options">A set of customizations for how the target object is registered. If <see langword="null"/>, default options will be used.</param>
    /// <param name="requestRevertOption"><see langword="true"/> to receive an <see cref="IDisposable"/> that can remove the target object; <see langword="false" /> otherwise.</param>
    /// <returns>An object that may be disposed of to revert the addition of the target object. Will be null if and only if <paramref name="requestRevertOption"/> is <see langword="false"/>.</returns>
    /// <remarks>
    /// When multiple target objects are added, the first target with a method that matches a request is invoked.
    /// </remarks>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    internal RpcTargetInfo.RevertAddLocalRpcTarget? AddLocalRpcTargetInternal(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type exposingMembersOn,
        object target,
        JsonRpcTargetOptions? options,
        bool requestRevertOption)
    {
        options ??= JsonRpcTargetOptions.Default;
        RpcTargetMetadata mapping =
            exposingMembersOn.IsInterface ? RpcTargetMetadata.FromInterface(exposingMembersOn) :
            options.AllowNonPublicInvocation ? RpcTargetMetadata.FromClassNonPublic(exposingMembersOn) :
            RpcTargetMetadata.FromClass(exposingMembersOn);

        return this.rpcTargetInfo.AddLocalRpcTarget(mapping, target, options, requestRevertOption);
    }

    internal RpcTargetInfo.RevertAddLocalRpcTarget? AddLocalRpcTargetInternal(RpcTargetMetadata exposingMembersOn, object target, JsonRpcTargetOptions? options, bool requestRevertOption)
        => this.rpcTargetInfo.AddLocalRpcTarget(exposingMembersOn, target, options ?? JsonRpcTargetOptions.Default, requestRevertOption);

    /// <summary>
    /// Adds a new RPC interface to an existing target registering additional RPC methods.
    /// </summary>
    /// <param name="targetMetadata">The interface type whose members define the RPC accessible members of the <paramref name="target"/> object.</param>
    /// <param name="target">Target to invoke when incoming messages are received.</param>
    /// <param name="options">A set of customizations for how the target object is registered. If <see langword="null" />, default options will be used.</param>
    /// <param name="revertAddLocalRpcTarget">
    /// An optional object that may be disposed of to revert the addition of the target object.
    /// </param>
    internal void AddRpcInterfaceToTargetInternal(RpcTargetMetadata targetMetadata, object target, JsonRpcTargetOptions? options, RpcTargetInfo.RevertAddLocalRpcTarget? revertAddLocalRpcTarget)
    {
        options ??= JsonRpcTargetOptions.Default;
        this.rpcTargetInfo.AddRpcInterfaceToTarget(targetMetadata, target, options, revertAddLocalRpcTarget);
    }

    /// <summary>
    /// Attempts to load a type based on its full name and possibly assembly name.
    /// </summary>
    /// <param name="typeFullName">The <see cref="Type.FullName"/> of the type to be loaded.</param>
    /// <param name="assemblyName">The assemble name that is expected to define the type, if available. This should be parseable by <see cref="AssemblyName(string)"/>.</param>
    /// <returns>The loaded <see cref="Type"/>, if one could be found; otherwise <see langword="null" />.</returns>
    /// <remarks>
    /// <para>
    /// This method is used to load types that are strongly referenced by incoming messages during serialization.
    /// It is important to not load types that may pose a security threat based on the type and the trust level of the remote party.
    /// </para>
    /// <para>
    /// The default implementation of this method loads any type named if it can be found based on its assembly name (if provided) or based on any assembly already loaded in the AppDomain otherwise.
    /// </para>
    /// <para>Implementations should avoid throwing <see cref="FileLoadException"/>, <see cref="TypeLoadException"/> or other exceptions, preferring to return <see langword="null" /> instead.</para>
    /// </remarks>
    [RequiresUnreferencedCode(RuntimeReasons.LoadType)]
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
    protected internal virtual Type? LoadType(string typeFullName, string? assemblyName)
    {
        Requires.NotNull(typeFullName, nameof(typeFullName));

        Assembly? typeDeclaringAssembly = null;
        if (assemblyName is object)
        {
            try
            {
                typeDeclaringAssembly = Assembly.Load(assemblyName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
                // Try removing the version from the AssemblyName and try again, in case the message came from a newer version.
                var an = new AssemblyName(assemblyName);
                if (an.Version is object)
                {
                    an.Version = null;
                    try
                    {
                        typeDeclaringAssembly = Assembly.Load(an.FullName);
                    }
                    catch (Exception exRedux) when (exRedux is FileNotFoundException or FileLoadException)
                    {
                        // If we fail again, we'll just try to load the exception type from the AppDomain without an assembly's context.
                    }
                }
            }
        }

        Type? runtimeType = typeDeclaringAssembly is object ? typeDeclaringAssembly.GetType(typeFullName) : Type.GetType(typeFullName);
        return runtimeType;
    }

    /// <summary>
    /// When overridden by a derived type, this attempts to load a type based on its full name and possibly assembly name.
    /// </summary>
    /// <param name="typeFullName">The <see cref="Type.FullName"/> of the type to be loaded.</param>
    /// <param name="assemblyName">The assemble name that is expected to define the type, if available. This should be parseable by <see cref="AssemblyName(string)"/>.</param>
    /// <returns>The loaded <see cref="Type"/>, if one could be found; otherwise <see langword="null" />.</returns>
    /// <remarks>
    /// <para>
    /// This method is used to load types that are strongly referenced by incoming messages during serialization.
    /// It is important to not load types that may pose a security threat based on the type and the trust level of the remote party.
    /// </para>
    /// <para>
    /// The default implementation of this method matches types registered with the <see cref="LoadableTypes"/> collection.
    /// </para>
    /// <para>Implementations should avoid throwing <see cref="FileLoadException"/>, <see cref="TypeLoadException"/> or other exceptions, preferring to return <see langword="null" /> instead.</para>
    /// </remarks>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
    protected internal virtual Type? LoadTypeTrimSafe(string typeFullName, string? assemblyName) => this.LoadableTypes.TryGetType(typeFullName, out Type? type) ? type : null;

    /// <summary>
    /// Disposes managed and native resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> if being disposed; <see langword="false"/> if being finalized.</param>
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
    /// <returns>The error details to return to the client. Must not be <see langword="null"/>.</returns>
    /// <remarks>
    /// This method may be overridden in a derived class to change the way error details are expressed.
    /// </remarks>
    /// <seealso cref="CreateExceptionFromRpcError(JsonRpcRequest, JsonRpcError)"/>
    protected virtual JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        Requires.NotNull(exception, nameof(exception));

        var localRpcEx = exception as LocalRpcException;
        bool iserializable = this.ExceptionStrategy == ExceptionProcessing.ISerializable;
        if (!ExceptionSerializationHelpers.IsSerializable(exception))
        {
            this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.ExceptionNotSerializable, "An exception of type {0} was thrown but is not serializable.", exception.GetType().AssemblyQualifiedName);
            iserializable = false;
        }

        return new JsonRpcError.ErrorDetail
        {
            Code = (JsonRpcErrorCode?)localRpcEx?.ErrorCode ?? (iserializable ? JsonRpcErrorCode.InvocationErrorWithException : JsonRpcErrorCode.InvocationError),
            Message = exception.Message,
            Data = localRpcEx is not null ? localRpcEx.ErrorData : (iserializable ? (object?)exception : new CommonErrorData(exception)),
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
        object? deserializedData = dataType is not null ? response.Error.GetData(dataType) : response.Error.Data;
        switch (response.Error.Code)
        {
            case JsonRpcErrorCode.InvalidParams:
            case JsonRpcErrorCode.MethodNotFound:
                return new RemoteMethodNotFoundException(response.Error.Message, request.Method, response.Error.Code, response.Error.Data, deserializedData);
            case JsonRpcErrorCode.ResponseSerializationFailure:
                return new RemoteSerializationException(response.Error.Message, response.Error.Data, deserializedData);

            default:
                return deserializedData is Exception innerException
                    ? new RemoteInvocationException(response.Error.Message, (int)response.Error.Code, innerException)
                    : new RemoteInvocationException(response.Error.Message, (int)response.Error.Code, response.Error.Data, deserializedData);
        }
    }

    /// <summary>
    /// Determines the type that the <see cref="JsonRpcError.ErrorDetail.Data"/> object should be deserialized to
    /// for an incoming <see cref="JsonRpcError"/> message.
    /// </summary>
    /// <param name="error">The received error message.</param>
    /// <returns>
    /// The type, or <see langword="null"/> if the type is unknown.
    /// </returns>
    /// <remarks>
    /// The default implementation matches what <see cref="CreateErrorDetails(JsonRpcRequest, Exception)"/> does
    /// by assuming that the <see cref="JsonRpcError.ErrorDetail.Data"/> object should be deserialized as an instance of <see cref="CommonErrorData"/>.
    /// However derived types can override this method and use <see cref="JsonRpcError.ErrorDetail.Code"/> or other means to determine the appropriate type.
    /// </remarks>
#pragma warning disable CA1716 // Identifiers should not match keywords
    protected virtual Type? GetErrorDetailsDataType(JsonRpcError error) => this.ExceptionStrategy == ExceptionProcessing.ISerializable && error?.Error?.Code == JsonRpcErrorCode.InvocationErrorWithException ? typeof(Exception) : typeof(CommonErrorData);
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
    /// <param name="arguments">Arguments to pass to the invoked method. They must be serializable using the selected <see cref="IJsonRpcMessageFormatter"/>. If <see langword="null"/>, no arguments are passed.</param>
    /// <param name="positionalArgumentDeclaredTypes">
    /// A list of <see cref="Type"/> objects that describe how each element in <paramref name="arguments"/> is expected by the server to be typed.
    /// If specified, this must have exactly the same length as <paramref name="arguments"/> and contain no <see langword="null"/> elements.
    /// This value is ignored when <paramref name="isParameterObject"/> is true.
    /// </param>
    /// <param name="namedArgumentDeclaredTypes">
    /// A dictionary of <see cref="Type"/> objects that describe how each entry in the <see cref="IReadOnlyDictionary{TKey, TValue}"/> provided in the only element of <paramref name="arguments"/> is expected by the server to be typed.
    /// If specified, this must have exactly the same set of keys as the dictionary contained in the first element of <paramref name="arguments"/>, and contain no <see langword="null"/> values.
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
    /// Thrown when the connection is terminated (by either side) before the request or while the request is in progress,
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

        JsonRpcRequest request = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateRequestMessage() ?? new JsonRpcRequest();
        request.RequestId = id;
        request.Method = targetName;
        this.ActivityTracingStrategy?.ApplyOutboundActivity(request);

        if (isParameterObject)
        {
            object? argument = arguments;
            if (arguments is not null)
            {
                if (arguments.Count != 1 || arguments[0] is null)
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
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Resources.ResponseUnexpectedFormat, response?.GetType().FullName ?? "(null)"));
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
    /// Invokes the method on the local RPC target object and converts the response into a JSON-RPC result message.
    /// </summary>
    /// <param name="request">The incoming JSON-RPC request that resulted in the <paramref name="targetMethod"/> being selected to receive the dispatch.</param>
    /// <param name="targetMethod">The method to be invoked and the arguments to pass to it.</param>
    /// <param name="cancellationToken">A cancellation token to pass to <see cref="TargetMethod.InvokeAsync(CancellationToken)"/>.</param>
    /// <returns>
    /// The JSON-RPC response message to send back to the client.
    /// This is never expected to be null. If the protocol indicates no response message is expected by the client, it will be dropped rather than transmitted.
    /// </returns>
    /// <remarks>
    /// Overrides of this method are expected to call this base method for core functionality.
    /// Overrides should call the base method before any yielding await in order to maintain consistent message ordering
    /// unless the goal of the override is specifically to alter ordering of incoming messages.
    /// </remarks>
    protected virtual async ValueTask<JsonRpcMessage> DispatchRequestAsync(JsonRpcRequest request, TargetMethod targetMethod, CancellationToken cancellationToken)
    {
        Requires.NotNull(request);
        Requires.NotNull(targetMethod);

        object? result;
        using IDisposable? activityTracingState = this.ActivityTracingStrategy?.ApplyInboundActivity(request);

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
            return this.CreateCancellationResponse(request);
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
                return this.CreateCancellationResponse(request);
            }

            JsonRpcResult resultMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateResultMessage() ?? new JsonRpcResult();
            resultMessage.RequestId = request.RequestId;
            resultMessage.Result = result;
            resultMessage.ResultDeclaredType = targetMethod.ReturnType;
            return resultMessage;
        }

        // Avoid another first chance exception from re-throwing here. We'll handle faults in our HandleInvocationTask* methods below.
        await resultingTask.NoThrowAwaitable(false);

        // Pick continuation delegate based on whether a Task.Result exists based on method declaration.
        // Checking on the runtime result object itself is problematic because .NET / C# implements
        // async Task methods to return a Task<VoidTaskResult> instance, and we shouldn't consider
        // the VoidTaskResult internal struct as a meaningful result.
        return TryGetTaskOfTOrValueTaskOfTType(targetMethod.ReturnType!.GetTypeInfo(), out _, out _)
            ? await this.HandleInvocationTaskOfTResultAsync(request, resultingTask, cancellationToken).ConfigureAwait(false)
            : this.HandleInvocationTaskResult(request, resultingTask);
    }

    /// <summary>
    /// Sends the JSON-RPC message to <see cref="IJsonRpcMessageHandler"/> instance to be transmitted.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">A token to cancel the send request.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <remarks>
    /// Overrides of this method are expected to call this base method for core functionality.
    /// Overrides should call the base method before any yielding await in order to maintain consistent message ordering
    /// unless the goal of the override is specifically to alter ordering of outgoing messages.
    /// </remarks>
    protected virtual async ValueTask SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
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
    /// <param name="isValueTask">Receives a value indicating whether <paramref name="taskOfTTypeInfo"/> is a ValueTask (true) or Task (false).</param>
    /// <returns><see langword="true"/> if <see cref="Task{T}"/> could be found in the type hierarchy; otherwise <see langword="false"/>.</returns>
    private static bool TryGetTaskOfTOrValueTaskOfTType(TypeInfo taskTypeInfo, [NotNullWhen(true)] out TypeInfo? taskOfTTypeInfo, out bool isValueTask)
    {
        Requires.NotNull(taskTypeInfo, nameof(taskTypeInfo));

        // Make sure we're prepared for Task<T>-derived types, by walking back up to the actual type in order to find the Result property.
        TypeInfo? taskTypeInfoLocal = taskTypeInfo;
        while (taskTypeInfoLocal is not null)
        {
            if (taskTypeInfoLocal.IsGenericType)
            {
                Type genericTypeDefinition = taskTypeInfoLocal.GetGenericTypeDefinition();
                if (genericTypeDefinition == typeof(Task<>))
                {
                    isValueTask = false;
                    taskOfTTypeInfo = taskTypeInfoLocal;
                    return true;
                }
                else if (genericTypeDefinition == typeof(ValueTask<>))
                {
                    isValueTask = true;
                    taskOfTTypeInfo = taskTypeInfoLocal;
                    return true;
                }
            }

            taskTypeInfoLocal = taskTypeInfoLocal.BaseType?.GetTypeInfo();
        }

        taskOfTTypeInfo = null;
        isValueTask = false;
        return false;
    }

    /// <summary>
    /// Convert a <see cref="ValueTask"/> or <see cref="ValueTask{T}"/> into a <see cref="Task"/> if possible.
    /// </summary>
    /// <param name="result">The result from the RPC method invocation.</param>
    /// <param name="task">Receives the converted <see cref="Task"/> object, if conversion was possible; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if conversion succeeded; <see langword="false"/> otherwise.</returns>
    private static bool TryGetTaskFromValueTask(object? result, [NotNullWhen(true)] out Task? task)
    {
        if (result is ValueTask resultingValueTask)
        {
            task = resultingValueTask.AsTask();
            return true;
        }

        if (result is not null)
        {
            TypeInfo resultTypeInfo = result.GetType().GetTypeInfo();
            if (resultTypeInfo.IsGenericType && resultTypeInfo.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                MethodInfo valueTaskAsTaskMethodInfo =
#if NET
                    (MethodInfo)resultTypeInfo.GetMemberWithSameMetadataDefinitionAs(ValueTaskAsTaskMethodInfo);
#else
                    resultTypeInfo.GetDeclaredMethod(nameof(ValueTask<int>.AsTask))!;
#endif
                task = (Task)valueTaskAsTaskMethodInfo.Invoke(result, Array.Empty<object>())!;
                return true;
            }
        }

        task = null;
        return false;
    }

    private JsonRpcError CreateCancellationResponse(JsonRpcRequest request)
    {
        JsonRpcError errorMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateErrorMessage() ?? new JsonRpcError();
        errorMessage.RequestId = request.RequestId;
        errorMessage.Error = new JsonRpcError.ErrorDetail
        {
            Code = JsonRpcErrorCode.RequestCanceled,
            Message = Resources.TaskWasCancelled,
        };
        return errorMessage;
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
                        JsonRpcEventSource.Instance.SendingNotification(request.Method, JsonRpcEventSource.GetArgumentsString(request));
                    }

                    // IMPORTANT: This should be the first await in this async code path.
                    //            This is crucial to the guarantee that overrides of SendAsync can assume they are executed
                    //            before the first await when a JsonRpc call is made.
                    await this.SendAsync(request, cts.Token).ConfigureAwait(false);
                    return default;
                }

                Verify.Operation(this.readLinesTask is not null, Resources.InvalidBeforeListenHasStarted);
                var tcs = new TaskCompletionSource<JsonRpcMessage>();
                Action<JsonRpcMessage?> dispatcher = (response) =>
                {
                    lock (this.dispatcherMapLock)
                    {
                        this.resultDispatcherMap.Remove(request.RequestId);
                    }

                    try
                    {
                        if (response is null)
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
                            if (error.Error is not null && JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
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
                    catch (Exception ex)
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
                        JsonRpcEventSource.Instance.SendingRequest(request.RequestId.NumberIfPossibleForEvent, request.Method, JsonRpcEventSource.GetArgumentsString(request));
                    }

                    string? parentToken = this.JoinableTaskFactory is not null ? this.JoinableTaskFactory.Context.Capture() : this.JoinableTaskTracker.Token;
                    if (parentToken is not null)
                    {
                        request.TrySetTopLevelProperty(JoinableTaskTokenHeaderName, parentToken);
                    }

                    // IMPORTANT: This should be the first await in this async code path.
                    //            This is crucial to the guarantee that overrides of SendAsync can assume they are executed
                    //            before the first await when a JsonRpc call is made.
                    await this.SendAsync(request, cts.Token).ConfigureAwait(false);
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
                    using (cancellationToken.Register(this.cancelPendingOutboundRequestAction!, request.RequestId, useSynchronizationContext: false))
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
        JsonRpcError errorMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateErrorMessage() ?? new JsonRpcError();
        errorMessage.RequestId = request.RequestId;
        errorMessage.Error = errorDetails;
        return errorMessage;
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

        JsonRpcError errorMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateErrorMessage() ?? new JsonRpcError();
        errorMessage.RequestId = request.RequestId;
        errorMessage.Error = errorDetails;
        return errorMessage;
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
        Requires.Argument(request.Method is not null, nameof(request), "Method must be set.");

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
                localMethodCancellationSource = new CancellationTokenSource();
                cancellationToken = localMethodCancellationSource.Token;

                if (this.CancelLocallyInvokedMethodsWhenConnectionIsClosed)
                {
                    // We do NOT use CancellationTokenSource.CreateLinkedToken because that link is unbreakable,
                    // and we need to break the link (but without disposing the CTS we created) at the conclusion of this method.
                    // Disposing the CTS causes .NET Framework (in its default configuration) to throw when CancellationToken.Register is called later,
                    // which causes problems with some long-lived server methods such as those that return IAsyncEnumerable<T>.
                    disconnectedRegistration = this.DisconnectedToken.Register(state => ((CancellationTokenSource)state!).Cancel(), localMethodCancellationSource);
                }
            }

            if (this.rpcTargetInfo.TryGetTargetMethod(request, out TargetMethod? targetMethod) && targetMethod.IsFound)
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
                        JsonRpcEventSource.Instance.ReceivedRequest(request.RequestId.NumberIfPossibleForEvent, request.Method, JsonRpcEventSource.GetArgumentsString(request));
                    }
                    else
                    {
                        JsonRpcEventSource.Instance.ReceivedNotification(request.Method, JsonRpcEventSource.GetArgumentsString(request));
                    }
                }

                request.TryGetTopLevelProperty<string>(JoinableTaskTokenHeaderName, out string? parentToken);
                if (this.JoinableTaskFactory is null)
                {
                    this.JoinableTaskTracker.Token = parentToken;
                }

                if (this.JoinableTaskFactory is null || parentToken is null)
                {
                    return await this.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    return await this.JoinableTaskFactory.RunAsync(
                        async () => await this.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false),
                        parentToken,
                        JoinableTaskCreationOptions.None);
                }
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

                        if (remoteResponse is JsonRpcError error && error.Error is not null)
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

                    if (remoteResponse is not null)
                    {
                        if (remoteResponse is IJsonRpcMessageWithId messageWithId)
                        {
                            messageWithId.RequestId = previousId;
                        }

                        return remoteResponse;
                    }
                }

                if (targetMethod is null)
                {
                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                    {
                        if (request.Method == MessageFormatterProgressTracker.ProgressRequestSpecialMethod)
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Verbose, (int)TraceEvents.RequestWithoutMatchingTarget, "No target methods are registered that match \"{0}\". This is expected since the formatter is expected to have intercepted this special method and dispatched to a local IProgress<T> object.", request.Method);
                        }
                        else
                        {
                            this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestWithoutMatchingTarget, "No target methods are registered that match \"{0}\".", request.Method);
                        }
                    }

                    if (!request.IsResponseExpected)
                    {
                        return DroppedError;
                    }

                    JsonRpcError errorMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateErrorMessage() ?? new JsonRpcError();
                    errorMessage.RequestId = request.RequestId;
                    errorMessage.Error = new JsonRpcError.ErrorDetail
                    {
                        Code = JsonRpcErrorCode.MethodNotFound,
                        Message = string.Format(CultureInfo.CurrentCulture, Resources.RpcMethodNameNotFound, request.Method),
                    };
                    return errorMessage;
                }
                else
                {
                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Warning))
                    {
                        this.TraceSource.TraceEvent(TraceEventType.Warning, (int)TraceEvents.RequestWithoutMatchingTarget, "Invocation of \"{0}\" cannot occur because arguments do not match any registered target methods.", request.Method);
                    }

                    if (!request.IsResponseExpected)
                    {
                        return DroppedError;
                    }

                    JsonRpcError errorMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateErrorMessage() ?? new JsonRpcError();
                    errorMessage.RequestId = request.RequestId;
                    errorMessage.Error = new JsonRpcError.ErrorDetail
                    {
                        Code = JsonRpcErrorCode.InvalidParams,
                        Message = targetMethod.LookupErrorMessage,
                        Data = targetMethod.ArgumentDeserializationFailures is object ? new CommonErrorData(targetMethod.ArgumentDeserializationFailures) : null,
                    };
                    return errorMessage;
                }
            }
        }
        catch (Exception ex) when (!this.IsFatalException(StripExceptionToInnerException(ex)))
        {
            JsonRpcError error = this.CreateError(request, ex);

            if (error.Error is not null && JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
            {
                JsonRpcEventSource.Instance.SendingError(request.RequestId.NumberIfPossibleForEvent, error.Error.Code);
            }

            return error;
        }
        finally
        {
            if (localMethodCancellationSource is not null)
            {
                this.CancellationStrategy?.IncomingRequestEnded(request.RequestId);

                // Be sure to dispose the link to the local method token we created in case it is linked to our long-lived disposal token
                // and otherwise cause a memory leak.
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
                await disconnectedRegistration.DisposeAsync().ConfigureAwait(false);
#else
                disconnectedRegistration.Dispose();
#endif
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
            result = this.CreateCancellationResponse(request);
        }
        else
        {
            JsonRpcResult resultMessage = (this.MessageHandler.Formatter as IJsonRpcMessageFactory)?.CreateResultMessage() ?? new JsonRpcResult();
            resultMessage.RequestId = request.RequestId;
            result = resultMessage;
        }

        if (result is JsonRpcError error)
        {
            if (error.Error is not null && JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None))
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
        Assumes.True(TryGetTaskOfTOrValueTaskOfTType(t.GetType().GetTypeInfo(), out TypeInfo? taskOfTTypeInfo, out bool isValueTask));

        object? result = null;
        Type? declaredResultType = null;
        if (t.Status == TaskStatus.RanToCompletion)
        {
            // If t is a Task<SomeType>, it will have Result property.
            // If t is just a Task, there is no Result property on it.
            // We can't really write direct code to deal with Task<T>, since we have no idea of T in this context, so we simply use reflection to
            // read the result at runtime.
#if NET
            MethodInfo resultGetter = isValueTask
                ? (MethodInfo)taskOfTTypeInfo.GetMemberWithSameMetadataDefinitionAs(ValueTaskGetResultMethodInfo)
                : (MethodInfo)taskOfTTypeInfo.GetMemberWithSameMetadataDefinitionAs(TaskGetResultMethodInfo);

            declaredResultType = resultGetter.ReturnType;
            result = resultGetter.Invoke(t, Array.Empty<object>());
#else
#pragma warning disable VSTHRD103 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
            const string ResultPropertyName = nameof(Task<int>.Result);
#pragma warning restore VSTHRD103

            PropertyInfo? resultProperty = taskOfTTypeInfo.GetDeclaredProperty(ResultPropertyName);
            Assumes.NotNull(resultProperty);
            declaredResultType = resultProperty.PropertyType;
            result = resultProperty.GetValue(t);
#endif

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
            if (this.disconnectedEventArgs is not null)
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

        this.rpcTargetInfo.UnregisterEventHandlersFromTargetObjects();

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

        try
        {
            await this.rpcTargetInfo.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }

        // Ensure the Task we may have returned from Completion is completed,
        // but not before any asynchronous disposal of our message handler completes.
        try
        {
            await messageHandlerDisposal.ConfigureAwait(false);
        }
        catch (Exception ex)
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
            Exception? loopBreakingException = null;

            while (!this.IsDisposed && !this.DisconnectedToken.IsCancellationRequested)
            {
                JsonRpcMessage? protocolMessage = null;
                try
                {
                    protocolMessage = await this.MessageHandler.ReadAsync(this.DisconnectedToken).ConfigureAwait(false);
                    if (protocolMessage is null)
                    {
                        this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(Resources.ReachedEndOfStream, DisconnectedReason.RemotePartyTerminated));
                        return;
                    }
                }
                catch (OperationCanceledException ex) when (this.DisconnectedToken.IsCancellationRequested)
                {
                    loopBreakingException = ex;
                    break;
                }
                catch (ObjectDisposedException ex) when (this.IsDisposed)
                {
                    loopBreakingException = ex;
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

                // We must clear buffers before reading the next message.
                // HandleRpcAsync must do whatever deserialization it requires before it yields.
                (this.MessageHandler as IJsonRpcMessageBufferManager)?.DeserializationComplete(protocolMessage);
            }

            this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.LocallyDisposed, loopBreakingException));
        }
        catch (Exception ex)
        {
            // Report the exception and kill the connection.
            // Do not *re-throw* the exception from here to avoid an unobserved exception being reported
            // (https://github.com/microsoft/vs-streamjsonrpc/issues/1067).
            this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(ex.Message, DisconnectedReason.StreamError, ex));
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
                        await this.SendAsync(result, this.DisconnectedToken).ConfigureAwait(false);
                        responseSent = true;
                    }
                    catch (OperationCanceledException) when (this.DisconnectedToken.IsCancellationRequested)
                    {
                    }
                    catch (ObjectDisposedException) when (this.IsDisposed)
                    {
                    }
                    catch (Exception exception)
                    {
                        // Some exceptions are fatal. If we aren't already disconnected, try sending an apology to the client.
                        if (!this.DisconnectedToken.IsCancellationRequested)
                        {
                            result = this.CreateErrorForResponseTransmissionFailure(request, exception);
                            await this.SendAsync(result, this.DisconnectedToken).ConfigureAwait(false);
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
                try
                {
                    JsonRpcResult? result = resultOrError as JsonRpcResult;
                    JsonRpcError? error = resultOrError as JsonRpcError;

                    lock (this.dispatcherMapLock)
                    {
#if NET
                        this.resultDispatcherMap.Remove(resultOrError.RequestId, out data);
#else
                        if (this.resultDispatcherMap.TryGetValue(resultOrError.RequestId, out data))
                        {
                            this.resultDispatcherMap.Remove(resultOrError.RequestId);
                        }
#endif
                    }

                    if (this.TraceSource.Switch.ShouldTrace(TraceEventType.Information))
                    {
                        if (result is not null)
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
                        if (data.ExpectedResultType is not null && rpc is JsonRpcResult resultMessage)
                        {
                            resultMessage.SetExpectedResultType(data.ExpectedResultType);
                        }
                        else if (rpc is JsonRpcError errorMessage && errorMessage.Error is not null)
                        {
                            Type? errorType = this.GetErrorDetailsDataType(errorMessage);
                            if (errorType is not null)
                            {
                                errorMessage.Error.SetExpectedDataType(errorType);
                            }
                        }

                        this.OnResponseReceived(rpc);

                        // Complete the caller's request with the response asynchronously so it doesn't delay handling of other JsonRpc messages.
                        await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                        data.CompletionHandler(rpc);
                        data = null; // avoid invoking again if we throw later
                    }
                    else
                    {
                        this.OnResponseReceived(rpc);

                        // Unexpected "response" to no request we have a record of. Raise disconnected event.
                        this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(
                            Resources.UnexpectedResponseWithNoMatchingRequest,
                            DisconnectedReason.RemoteProtocolViolation));
                    }
                }
                catch
                {
                    this.OnResponseReceived(rpc);
                    throw;
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

    /// <summary>
    /// Throws an exception if we have already started listening,
    /// unless <see cref="AllowModificationWhileListening"/> is <see langword="true"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="HasListeningStarted"/> is <see langword="true"/> and <see cref="AllowModificationWhileListening"/> is <see langword="false"/>.</exception>
    private void ThrowIfConfigurationLocked()
    {
        // PERF: This can get called a lot in scenarios where connections are short-lived and frequent.
        // Avoid loading the string resource unless we're going to throw the exception.
        if (this.HasListeningStarted && !this.AllowModificationWhileListening)
        {
            Verify.FailOperation(Resources.MustNotBeListening);
        }
    }

    /// <summary>
    /// An object that correlates <see cref="JoinableTask"/> tokens within and between <see cref="JsonRpc"/> instances
    /// within a process that does <em>not</em> use <see cref="JoinableTaskFactory"/>,
    /// for purposes of mitigating deadlocks in processes that <em>do</em> use <see cref="JoinableTaskFactory"/>.
    /// </summary>
    public class JoinableTaskTokenTracker
    {
        /// <summary>
        /// The default instance to use.
        /// </summary>
        internal static readonly JoinableTaskTokenTracker Default = new JoinableTaskTokenTracker();

        /// <summary>
        /// Carries the value from a <see cref="JoinableTaskTokenHeaderName"/> when <see cref="JoinableTaskFactory"/> has not been set.
        /// </summary>
        private readonly System.Threading.AsyncLocal<string?> joinableTaskTokenWithoutJtf = new();

        internal string? Token
        {
            get => this.joinableTaskTokenWithoutJtf.Value;
            set => this.joinableTaskTokenWithoutJtf.Value = value;
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

    [RequiresUnreferencedCode(RuntimeReasons.LoadType)]
    private class NotTrimSafeTypeLoader(JsonRpc jsonRpc) : ExceptionSerializationHelpers.IExceptionTypeLoader
    {
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        public Type? Load(string typeName, string? assemblyName) => jsonRpc.LoadType(typeName, assemblyName);
    }
}
