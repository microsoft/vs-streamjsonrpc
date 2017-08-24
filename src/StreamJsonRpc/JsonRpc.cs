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
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

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
        private readonly object callbackTarget;

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
        private readonly Dictionary<int, OutstandingCallData> resultDispatcherMap = new Dictionary<int, OutstandingCallData>();

        /// <summary>
        /// A map of id's from inbound calls that have not yet completed and may be canceled,
        /// to their <see cref="CancellationTokenSource"/> instances.
        /// Lock the <see cref="dispatcherMapLock"/> object for all access to this member.
        /// </summary>
        private readonly Dictionary<JToken, CancellationTokenSource> inboundCancellationSources = new Dictionary<JToken, CancellationTokenSource>(JToken.EqualityComparer);

        /// <summary>
        /// A delegate for the <see cref="CancelPendingOutboundRequest"/> method.
        /// </summary>
        private readonly Action<object> cancelPendingOutboundRequestAction;

        /// <summary>
        /// A delegate for the <see cref="HandleInvocationTaskResult(JToken, Task)"/> method.
        /// </summary>
        private readonly Func<Task, object, JsonRpcMessage> handleInvocationTaskResultDelegate;

        /// <summary>
        /// A dictionary to map clr method names to <see cref="JsonRpcMethodAttribute" /> values.
        /// </summary>
        private readonly ReadOnlyDictionary<string, string> requestMethodToClrMethodMap;

        private readonly CancellationTokenSource disposeCts = new CancellationTokenSource();

        private Task readLinesTask;
        private int nextId = 1;
        private bool disposed;
        private bool hasDisconnectedEventBeenRaised;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class that uses
        /// <see cref="HeaderDelimitedMessageHandler"/> for encoding/decoding messages.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <remarks>
        /// It is important to call <see cref="StartListening"/> to begin receiving messages.
        /// </remarks>
        public JsonRpc(Stream sendingStream, Stream receivingStream, object target = null)
            : this(new HeaderDelimitedMessageHandler(sendingStream, receivingStream), target)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class.
        /// </summary>
        /// <param name="messageHandler">The message handler to use to transmit and receive RPC messages.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <remarks>
        /// It is important to call <see cref="StartListening"/> to begin receiving messages.
        /// </remarks>
        public JsonRpc(DelimitedMessageHandler messageHandler, object target = null)
        {
            Requires.NotNull(messageHandler, nameof(messageHandler));

            this.cancelPendingOutboundRequestAction = this.CancelPendingOutboundRequest;
            this.handleInvocationTaskResultDelegate = (t, id) => this.HandleInvocationTaskResult((JToken)id, t);

            this.MessageHandler = messageHandler;
            this.callbackTarget = target;
            this.MessageJsonSerializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            };
            this.MessageJsonDeserializerSettings = new JsonSerializerSettings
            {
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                Converters = this.MessageJsonSerializerSettings.Converters,
            };
            this.JsonSerializer = new JsonSerializer();

            this.requestMethodToClrMethodMap = this.callbackTarget != null
                ? new ReadOnlyDictionary<string, string>(GetRequestMethodToClrMethodMap(this.callbackTarget))
                : EmptyDictionary;
        }

        /// <summary>
        /// Raised when the underlying stream is disconnected.
        /// </summary>
        public event EventHandler<JsonRpcDisconnectedEventArgs> Disconnected
        {
            add
            {
                Requires.NotNull(value, nameof(value));
                bool handlerAdded = false;
                lock (this.disconnectedEventLock)
                {
                    if (!this.hasDisconnectedEventBeenRaised)
                    {
                        this.DisconnectedPrivate += value;
                        handlerAdded = true;
                    }
                }

                if (!handlerAdded)
                {
                    value(this, new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.Disposed));
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
        /// Gets or sets the encoding to use for transmitted JSON messages.
        /// </summary>
        public Encoding Encoding
        {
            get { return this.MessageHandler.Encoding; }
            set { this.MessageHandler.Encoding = value; }
        }

        /// <summary>
        /// Gets the message handler used to send and receive messages.
        /// </summary>
        public DelimitedMessageHandler MessageHandler { get; }

        /// <inheritdoc />
        bool IDisposableObservable.IsDisposed => this.disposeCts.IsCancellationRequested;

        /// <summary>
        /// Gets the <see cref="JsonSerializer"/> used when serializing and deserializing method arguments and return values.
        /// </summary>
        public JsonSerializer JsonSerializer { get; }

        private JsonSerializerSettings MessageJsonSerializerSettings { get; }

        private JsonSerializerSettings MessageJsonDeserializerSettings { get; }

        private Formatting JsonSerializerFormatting { get; set; } = Formatting.Indented;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class and immediately starts listening.
        /// </summary>
        /// <param name="stream">A bidirectional stream to send and receive RPC messages on.</param>
        /// <param name="target">An optional target object to invoke when incoming RPC requests arrive.</param>
        /// <returns>The initialized and listening <see cref="JsonRpc"/> object.</returns>
        public static JsonRpc Attach(Stream stream, object target = null)
        {
            Requires.NotNull(stream, nameof(stream));

            return Attach(stream, stream, target);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpc"/> class and immediately starts listening.
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
        /// Starts listening to incoming messages.
        /// </summary>
        public void StartListening()
        {
            Verify.Operation(this.MessageHandler.CanRead, Resources.StreamMustBeReadable);
            Verify.Operation(this.readLinesTask == null, Resources.InvalidAfterListenHasStarted);
            Verify.NotDisposed(this);
            this.readLinesTask = Task.Run(this.ReadAndHandleRequestsAsync, this.disposeCts.Token);
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
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
        /// <typeparam name="TResult">Type of the method result</typeparam>
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
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
        /// <typeparam name="TResult">Type of the method result</typeparam>
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
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
        /// Invoke a method on the server and get back the result.  The parameter is passed as an object.
        /// </summary>
        /// <typeparam name="TResult">Type of the method result</typeparam>
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object argument = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // If argument is null, this indicates that the method does not take any parameters.
            object[] argumentToPass = argument == null ? null : new object[] { argument };
            int id = Interlocked.Increment(ref this.nextId);
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
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
        /// <typeparam name="TResult">Type of the method result</typeparam>
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
        /// Result task fails with this exception if the <paramref name="targetName"/> method is not found on the target object on the server.
        /// </exception>
        /// <exception cref="RemoteTargetNotSetException">
        /// Result task fails with this exception if the server has no target object.
        /// </exception>
        /// <exception cref="ArgumentNullException">If <paramref name="targetName"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">If this instance of <see cref="JsonRpc"/> has been disposed.</exception>
        public Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object> arguments = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            int id = Interlocked.Increment(ref this.nextId);
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
            if (!this.disposed)
            {
                this.disposed = true;
                if (disposing)
                {
                    var disconnectedEventArgs = new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.Disposed);
                    this.OnJsonRpcDisconnected(disconnectedEventArgs);
                }
            }
        }

        /// <summary>
        /// Invokes the specified RPC method
        /// </summary>
        /// <typeparam name="TResult">RPC method return type</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName">Name of the method to invoke.</param>
        /// <param name="arguments">Arguments to pass to the invoked method. If null, no arguments are passed.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <returns>A task whose result is the deserialized response from the JSON-RPC server.</returns>
        protected virtual Task<TResult> InvokeCoreAsync<TResult>(int? id, string targetName, IReadOnlyList<object> arguments, CancellationToken cancellationToken)
        {
            return this.InvokeCoreAsync<TResult>(id, targetName, arguments, cancellationToken, isParameterObject: false);
        }

        /// <summary>
        /// Invokes the specified RPC method
        /// </summary>
        /// <typeparam name="TResult">RPC method return type</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName">Name of the method to invoke.</param>
        /// <param name="arguments">Arguments to pass to the invoked method. If null, no arguments are passed.</param>
        /// <param name="cancellationToken">The token whose cancellation should signal the server to stop processing this request.</param>
        /// <param name="isParameterObject">Value which indicates if parameter should be passed as an object.</param>
        /// <returns>A task whose result is the deserialized response from the JSON-RPC server.</returns>
        protected virtual async Task<TResult> InvokeCoreAsync<TResult>(int? id, string targetName, IReadOnlyList<object> arguments, CancellationToken cancellationToken, bool isParameterObject)
        {
            Requires.NotNullOrEmpty(targetName, nameof(targetName));

            Verify.NotDisposed(this);
            cancellationToken.ThrowIfCancellationRequested();

            JsonRpcMessage request;
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

                request = JsonRpcMessage.CreateRequestWithNamedParameters(id, targetName, argument, this.JsonSerializer);
            }
            else
            {
                arguments = arguments ?? EmptyObjectArray;

                request = JsonRpcMessage.CreateRequest(id, targetName, arguments, this.JsonSerializer);
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.disposeCts.Token))
            {
                if (id == null)
                {
                    await this.TransmitAsync(request, cts.Token).ConfigureAwait(false);
                    return default(TResult);
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
                            tcs.TrySetCanceled();
                        }
                        else if (response.IsError)
                        {
                            tcs.TrySetException(CreateExceptionFromRpcError(response, targetName));
                        }
                        else
                        {
                            tcs.TrySetResult(response.GetResult<TResult>(this.JsonSerializer));
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

                await this.TransmitAsync(request, cts.Token).ConfigureAwait(false);

                // Arrange for sending a cancellation message if canceled while we're waiting for a response.
                using (cancellationToken.Register(this.cancelPendingOutboundRequestAction, id.Value, useSynchronizationContext: false))
                {
                    // This task will be completed when the Response object comes back from the other end of the pipe
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Creates a dictionary which maps a request method name to its clr method name via <see cref="JsonRpcMethodAttribute" /> value.
        /// </summary>
        /// <param name="target">Object to reflect over and analyze its methods.</param>
        /// <returns>Dictionary which maps a request method name to its clr method name.</returns>
        private static IDictionary<string, string> GetRequestMethodToClrMethodMap(object target)
        {
            Requires.NotNull(target, nameof(target));

            var clrMethodToRequestMethodMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var requestMethodToClrMethodMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var candidateAliases = new Dictionary<string, string>(StringComparer.Ordinal);

            for (TypeInfo t = target.GetType().GetTypeInfo(); t != null; t = t.BaseType?.GetTypeInfo())
            {
                foreach (MethodInfo method in t.DeclaredMethods)
                {
                    var attribute = (JsonRpcMethodAttribute)method.GetCustomAttribute(typeof(JsonRpcMethodAttribute));
                    var requestName = attribute?.Name ?? method.Name;

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
                    if (requestMethodToClrMethodMap.TryGetValue(requestName, out string previousClrNameUse))
                    {
                        if (!string.Equals(method.Name, previousClrNameUse, StringComparison.Ordinal))
                        {
                            Requires.Fail(Resources.ConflictingMethodAttributeValue, method.Name, previousClrNameUse, requestName);
                        }
                    }
                    else
                    {
                        requestMethodToClrMethodMap.Add(requestName, method.Name);
                    }

                    // If no explicit attribute has been applied, and the method ends with Async,
                    // register a request method name that does not include Async as well.
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
                if (!requestMethodToClrMethodMap.ContainsKey(candidateAlias.Key))
                {
                    requestMethodToClrMethodMap.Add(candidateAlias.Key, candidateAlias.Value);
                }
            }

            return requestMethodToClrMethodMap;
        }

        private static RemoteRpcException CreateExceptionFromRpcError(JsonRpcMessage response, string targetName)
        {
            Requires.NotNull(response, nameof(response));
            Requires.Argument(response.IsError, nameof(response), Resources.ResponseIsNotError);

            switch (response.Error.Code)
            {
                case (int)JsonRpcErrorCode.MethodNotFound:
                    return new RemoteMethodNotFoundException(response.Error.Message, targetName);

                case (int)JsonRpcErrorCode.NoCallbackObject:
                    return new RemoteTargetNotSetException(response.Error.Message);

                default:
                    return new RemoteInvocationException(response.Error.Message, response.Error.ErrorStack, response.Error.ErrorCode);
            }
        }

        private static JsonRpcMessage CreateError(JToken id, Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (exception is TargetInvocationException || (exception is AggregateException && exception.InnerException != null))
            {
                // Never let the outer (TargetInvocationException) escape because the inner is the interesting one to the caller, the outer is due to
                // the fact we are using reflection.
                exception = exception.InnerException;
            }

            var data = new { stack = exception.StackTrace, code = exception.HResult.ToString(CultureInfo.InvariantCulture) };
            return JsonRpcMessage.CreateError(id, JsonRpcErrorCode.InvocationError, exception.Message, JObject.FromObject(data, DefaultJsonSerializer));
        }

        private async Task<JsonRpcMessage> DispatchIncomingRequestAsync(JsonRpcMessage request, JsonSerializer jsonSerializer)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));

            if (this.callbackTarget == null)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Resources.DroppingRequestDueToNoTargetObject, request.Method);
                return JsonRpcMessage.CreateError(request.Id, JsonRpcErrorCode.NoCallbackObject, message);
            }

            bool ctsAdded = false;
            try
            {
                var targetMethod = new TargetMethod(request, this.callbackTarget, jsonSerializer, this.requestMethodToClrMethodMap);
                if (!targetMethod.IsFound)
                {
                    return JsonRpcMessage.CreateError(request.Id, JsonRpcErrorCode.MethodNotFound, targetMethod.LookupErrorMessage);
                }

                var cancellationToken = CancellationToken.None;
                if (targetMethod.AcceptsCancellationToken && !request.IsNotification)
                {
                    var cts = new CancellationTokenSource();
                    cancellationToken = cts.Token;
                    lock (this.dispatcherMapLock)
                    {
                        this.inboundCancellationSources.Add(request.Id, cts);
                        ctsAdded = true;
                    }
                }

                object result = targetMethod.Invoke(cancellationToken);
                if (!(result is Task))
                {
                    return JsonRpcMessage.CreateResult(request.Id, result, this.JsonSerializer);
                }

                return await ((Task)result).ContinueWith(this.handleInvocationTaskResultDelegate, request.Id, TaskScheduler.Default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return CreateError(request.Id, ex);
            }
            finally
            {
                if (ctsAdded)
                {
                    lock (this.dispatcherMapLock)
                    {
                        this.inboundCancellationSources.Remove(request.Id);
                    }
                }
            }
        }

        private JsonRpcMessage HandleInvocationTaskResult(JToken id, Task t)
        {
            if (t == null)
            {
                throw new ArgumentNullException(nameof(t));
            }

            if (!t.IsCompleted)
            {
                throw new ArgumentException(Resources.TaskNotCompleted, nameof(t));
            }

            if (t.IsFaulted)
            {
                return CreateError(id, t.Exception);
            }

            if (t.IsCanceled)
            {
                return JsonRpcMessage.CreateError(id, JsonRpcErrorCode.InvocationError, Resources.TaskWasCancelled);
            }

            object taskResult = null;
            Type taskType = t.GetType();

            // If t is a Task<SomeType>, it will have Result property.
            // If t is just a Task, there is no Result property on it.
            if (!taskType.Equals(typeof(Task)))
            {
#pragma warning disable VSTHRD002 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
#pragma warning disable VSTHRD102 // misfiring analyzer https://github.com/Microsoft/vs-threading/issues/60
                const string ResultPropertyName = nameof(Task<int>.Result);
#pragma warning restore VSTHRD002
#pragma warning restore VSTHRD102

                // We can't really write direct code to deal with Task<T>, since we have no idea of T in this context, so we simply use reflection to
                // read the result at runtime.
                PropertyInfo resultProperty = taskType.GetTypeInfo().GetDeclaredProperty(ResultPropertyName);
                taskResult = resultProperty?.GetValue(t);
            }

            return JsonRpcMessage.CreateResult(id, taskResult, this.JsonSerializer);
        }

        private void OnJsonRpcDisconnected(JsonRpcDisconnectedEventArgs eventArgs)
        {
            EventHandler<JsonRpcDisconnectedEventArgs> handlersToInvoke = null;
            lock (this.disconnectedEventLock)
            {
                if (!this.hasDisconnectedEventBeenRaised)
                {
                    this.hasDisconnectedEventBeenRaised = true;
                    handlersToInvoke = this.DisconnectedPrivate;
                    this.DisconnectedPrivate = null;
                }
            }

            try
            {
                // Fire the event first so that subscribers can interact with a non-disposed stream
                handlersToInvoke?.Invoke(this, eventArgs);
            }
            finally
            {
                // Dispose the stream and cancel pending requests in the finally block
                // So this is executed even if Disconnected event handler throws.
                this.disposeCts.Cancel();
                this.MessageHandler.Dispose();
                this.CancelPendingRequests();
            }
        }

        private async Task ReadAndHandleRequestsAsync()
        {
            JsonRpcDisconnectedEventArgs disconnectedEventArgs = null;

            try
            {
                while (!this.disposed)
                {
                    string json = null;
                    try
                    {
                        json = await this.MessageHandler.ReadAsync(this.disposeCts.Token).ConfigureAwait(false);
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
                            string.Format(CultureInfo.CurrentCulture, Resources.ReadingJsonRpcStreamFailed, exception.GetType().Name, exception.Message),
                            DisconnectedReason.StreamError,
                            exception);

                        // Fatal error. Raise disconnected event.
                        this.OnJsonRpcDisconnected(e);
                        break;
                    }

                    if (json == null)
                    {
                        // End of stream reached
                        disconnectedEventArgs = new JsonRpcDisconnectedEventArgs(Resources.ReachedEndOfStream, DisconnectedReason.Disposed);
                        break;
                    }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

                    Task.Run(
                        async delegate
                    {
                        try
                        {
                            await this.HandleRpcAsync(json).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            var eventArgs = new JsonRpcDisconnectedEventArgs(
                                string.Format(CultureInfo.CurrentCulture, Resources.UnexpectedErrorProcessingJsonRpc, json, exception.Message),
                                DisconnectedReason.ParseError,
                                json,
                                exception);

                            // Fatal error. Raise disconnected event.
                            this.OnJsonRpcDisconnected(eventArgs);
                        }
                    }, this.disposeCts.Token);

#pragma warning restore CS4014
                }
            }
            finally
            {
                if (disconnectedEventArgs == null)
                {
                    disconnectedEventArgs = new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.Disposed);
                }

                this.OnJsonRpcDisconnected(disconnectedEventArgs);
            }
        }

        private async Task HandleRpcAsync(string json)
        {
            JsonRpcMessage rpc;
            try
            {
                rpc = JsonRpcMessage.FromJson(json, this.MessageJsonDeserializerSettings);
            }
            catch (JsonException exception)
            {
                var e = new JsonRpcDisconnectedEventArgs(
                    string.Format(CultureInfo.CurrentCulture, Resources.FailureDeserializingJsonRpc, json, exception.Message),
                    DisconnectedReason.ParseError,
                    json,
                    exception);

                // Fatal error. Raise disconnected event.
                this.OnJsonRpcDisconnected(e);
                return;
            }

            if (rpc.IsRequest)
            {
                // We can't accept a request that requires a response if we can't write.
                Verify.Operation(rpc.IsNotification || this.MessageHandler.CanWrite, Resources.StreamMustBeWriteable);

                if (rpc.IsNotification && rpc.Method == CancelRequestSpecialMethod)
                {
                    this.HandleCancellationNotification(rpc);
                    return;
                }

                JsonRpcMessage result = await this.DispatchIncomingRequestAsync(rpc, this.JsonSerializer).ConfigureAwait(false);

                if (!rpc.IsNotification)
                {
                    try
                    {
                        await this.TransmitAsync(result, this.disposeCts.Token).ConfigureAwait(false);
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

                return;
            }

            if (rpc.IsResponse)
            {
                OutstandingCallData data = null;
                lock (this.dispatcherMapLock)
                {
                    int id = (int)rpc.Id;
                    if (this.resultDispatcherMap.TryGetValue(id, out data))
                    {
                        this.resultDispatcherMap.Remove(id);
                    }
                }

                if (data != null)
                {
                    data.CompletionHandler(rpc);
                }

                return;
            }

            // Not a request or return. Raise disconnected event.
            this.OnJsonRpcDisconnected(new JsonRpcDisconnectedEventArgs(
                string.Format(CultureInfo.CurrentCulture, Resources.UnrecognizedIncomingJsonRpc, json),
                DisconnectedReason.ParseError,
                json));
        }

        private void HandleCancellationNotification(JsonRpcMessage rpc)
        {
            Requires.NotNull(rpc, nameof(rpc));

            JToken id = rpc.Parameters.SelectToken("id");
            CancellationTokenSource cts;
            lock (this.dispatcherMapLock)
            {
                this.inboundCancellationSources.TryGetValue(id, out cts);
            }

            cts?.Cancel();
        }

        private void CancelPendingRequests()
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
            Task.Run(async delegate
            {
                try
                {
                    Requires.NotNull(state, nameof(state));
                    object id = state;
                    if (!this.disposed)
                    {
                        var cancellationMessage = JsonRpcMessage.CreateRequestWithNamedParameters(id: null, method: CancelRequestSpecialMethod, namedParameters: new { id = id }, parameterSerializer: DefaultJsonSerializer);
                        await this.TransmitAsync(cancellationMessage, this.disposeCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
#if NET45
                catch (Exception ex)
                {
                    Debug.Fail(ex.Message, ex.ToString());
                }
#else
                catch (Exception)
                {
                }
#endif
            });
        }

        private Task TransmitAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            string json = message.ToJson(this.JsonSerializerFormatting, this.MessageJsonSerializerSettings);
            return this.MessageHandler.WriteAsync(json, cancellationToken);
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
    }
}
