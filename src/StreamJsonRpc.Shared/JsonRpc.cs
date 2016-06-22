using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.VisualStudio.Threading;
using Microsoft;

namespace StreamJsonRpc
{
    public class JsonRpc : IDisposable
    {
        private class OutstandingCallData
        {
            internal object TaskCompletionSource { get; }

            internal Action<JsonRpcMessage> CompletionHandler { get; }

            internal OutstandingCallData(object taskCompletionSource, Action<JsonRpcMessage> completionHandler)
            {
                this.TaskCompletionSource = taskCompletionSource;
                this.CompletionHandler = completionHandler;
            }
        }

        private const int BufferSize = 1024;
        private readonly object callbackTarget;
        private readonly object dispatcherMapLock = new object();
        private readonly object disconnectedEventLock = new object();
        private readonly Dictionary<string, OutstandingCallData> resultDispatcherMap = new Dictionary<string, OutstandingCallData>(StringComparer.Ordinal);

        private readonly Task readLinesTask;
        private readonly CancellationTokenSource disposeCts = new CancellationTokenSource();

        private int nextId = 1;
        private bool disposed;
        private bool hasDisconnectedEventBeenRaised;

        private HeaderDelimitedMessages messageHandler;

        public static JsonRpc Attach(Stream stream, object target = null)
        {
            return Attach(stream, stream, target);
        }

        public static JsonRpc Attach(Stream sendingStream, Stream receivingStream, object target = null)
        {
            return new JsonRpc(sendingStream, receivingStream, target);
        }

        protected JsonRpc(Stream sendingStream, Stream receivingStream, object target = null)
        {
            Requires.NotNull(sendingStream, nameof(sendingStream));
            Requires.NotNull(receivingStream, nameof(receivingStream));

            this.messageHandler = new StreamJsonRpc.HeaderDelimitedMessages(sendingStream, receivingStream);

            this.callbackTarget = target;

            this.readLinesTask = Task.Run(this.ReadAndHandleRequestsAsync, this.disposeCts.Token);
        }

        private event EventHandler<JsonRpcDisconnectedEventArgs> onDisconnected;

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
                        this.onDisconnected += value;
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
                this.onDisconnected -= value;
            }
        }

        /// <summary>
        /// Gets or sets the encoding to use for transmitted JSON messages.
        /// </summary>
        public Encoding Encoding { get; set; } = Encoding.UTF8;

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
        /// <typeparam name="Result">Type of the method result</typeparam>
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
        public Task<Result> InvokeAsync<Result>(string targetName, params object[] arguments)
        {
            if (targetName == null)
            {
                throw new ArgumentNullException(nameof(targetName));
            }

            this.ThrowIfDisposed();
            string id = Interlocked.Increment(ref this.nextId).ToString(CultureInfo.InvariantCulture);
            return InvokeCoreAsync<Result>(id, targetName, arguments);
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
        public async Task NotifyAsync(string targetName, params object[] arguments)
        {
            if (targetName == null)
            {
                throw new ArgumentNullException(nameof(targetName));
            }

            this.ThrowIfDisposed();
            await this.InvokeCoreAsync<object>(id: null, targetName: targetName, arguments: arguments).ConfigureAwait(false);
        }

        #region IDisposable

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        private void OnJsonRpcDisconnected(JsonRpcDisconnectedEventArgs eventArgs)
        {
            EventHandler<JsonRpcDisconnectedEventArgs> handlersToInvoke = null;
            lock (this.disconnectedEventLock)
            {
                if (!this.hasDisconnectedEventBeenRaised)
                {
                    this.hasDisconnectedEventBeenRaised = true;
                    handlersToInvoke = this.onDisconnected;
                    this.onDisconnected = null;
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
                this.CancelPendingRequests();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                this.disposed = true;
                if (disposing)
                {
                    this.messageHandler.Dispose();
                    var disconnectedEventArgs = new JsonRpcDisconnectedEventArgs(Resources.StreamDisposed, DisconnectedReason.Disposed);
                    this.OnJsonRpcDisconnected(disconnectedEventArgs);
                }
            }
        }

        protected void ThrowIfDisposed()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }
        }

        /// <summary>
        /// Invokes the specified RPC method
        /// </summary>
        /// <typeparam name="ReturnType">RPC method return type</typeparam>
        /// <param name="id">An identifier established by the Client that MUST contain a String, Number, or NULL value if included.
        /// If it is not included it is assumed to be a notification.</param>
        /// <param name="targetName">RPC method name</param>
        /// <param name="arguments">RPC method arguments</param>
        /// <returns></returns>
        protected virtual async Task<ReturnType> InvokeCoreAsync<ReturnType>(string id, string targetName, params object[] arguments)
        {
            // If somebody calls InvokeInternal<T>(id, "method", null), the null is not passed as an item in the array.
            // Instead, the compiler thinks that the null is the array itself and it'll pass null directly.
            // To account for this case, we check for null below.
            arguments = arguments ?? new object[] { null };

            JsonRpcMessage request = JsonRpcMessage.CreateRequest(id, targetName, arguments);
            if (id == null)
            {
                await this.messageHandler.WriteAsync(request.ToJson(), this.disposeCts.Token).ConfigureAwait(false);
                return default(ReturnType);
            }

            var tcs = new TaskCompletionSource<ReturnType>();
            Action<JsonRpcMessage> dispatcher = (response) =>
            {
                lock (this.dispatcherMapLock)
                {
                    this.resultDispatcherMap.Remove(id);
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
                        tcs.TrySetResult(response.GetResult<ReturnType>());
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            lock (this.dispatcherMapLock)
            {
                this.resultDispatcherMap.Add(id, new OutstandingCallData(tcs, dispatcher));
            }

            await this.messageHandler.WriteAsync(request.ToJson(), this.disposeCts.Token).ConfigureAwait(false);

            // This task will be completed when the Response object comes back from the other end of the pipe
            await tcs.Task.NoThrowAwaitable(false);
            await Task.Yield(); // ensure we don't inline anything further, including the continuation of our caller.
            return await tcs.Task.ConfigureAwait(false);
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

        private async Task<JsonRpcMessage> DispatchIncomingRequestAsync(JsonRpcMessage request)
        {
            if (this.callbackTarget == null)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Resources.DroppingRequestDueToNoTargetObject, request.Method);
                return JsonRpcMessage.CreateError(request.Id, JsonRpcErrorCode.NoCallbackObject, message);
            }

            try
            {
                var targetMethod = new TargetMethod(request, this.callbackTarget);
                if (!targetMethod.IsFound)
                {
                    return JsonRpcMessage.CreateError(request.Id, JsonRpcErrorCode.MethodNotFound, targetMethod.LookupErrorMessage);
                }

                object result = targetMethod.Invoke();
                if (!(result is Task))
                {
                    return JsonRpcMessage.CreateResult(request.Id, result);
                }

                return await ((Task)result).ContinueWith((t, id) => HandleInvocationTaskResult((string)id, t), request.Id, TaskScheduler.Default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return CreateError(request.Id, ex);
            }
        }

        private static JsonRpcMessage HandleInvocationTaskResult(string id, Task t)
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
                const string ResultPropertyName = nameof(Task<int>.Result);

                // We can't really write direct code to deal with Task<T>, since we have no idea of T in this context, so we simply use reflection to
                // read the result at runtime.
                PropertyInfo resultProperty = taskType.GetTypeInfo().GetDeclaredProperty(ResultPropertyName);
                taskResult = resultProperty?.GetValue(t);
            }

            return JsonRpcMessage.CreateResult(id, taskResult);
        }

        private static JsonRpcMessage CreateError(string id, Exception exception)
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

            string message = $"{exception.Message}{Environment.NewLine}{exception.StackTrace}";
            var data = new { stack = exception.StackTrace, code = exception.HResult.ToString(CultureInfo.InvariantCulture) };
            return JsonRpcMessage.CreateError(id, JsonRpcErrorCode.InvocationError, message, data);
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
                        json = await this.messageHandler.ReadAsync(this.disposeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (Exception exception)
                    {
                        var e = new JsonRpcDisconnectedEventArgs(string.Format(CultureInfo.CurrentCulture, Resources.ReadingJsonRpcStreamFailed, exception.GetType().Name, exception.Message),
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

                    Task.Run(() =>
                    {
                        try
                        {
                            this.HandleRpcAsync(json);
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
                rpc = JsonRpcMessage.FromJson(json);
            }
            catch (JsonException exception)
            {
                var e = new JsonRpcDisconnectedEventArgs(string.Format(CultureInfo.CurrentCulture, Resources.FailureDeserializingJsonRpc, json, exception.Message),
                                                            DisconnectedReason.ParseError,
                                                            json,
                                                            exception);

                // Fatal error. Raise disconnected event.
                this.OnJsonRpcDisconnected(e);
                return;
            }

            if (rpc.IsRequest)
            {
                JsonRpcMessage result = await this.DispatchIncomingRequestAsync(rpc).ConfigureAwait(false);

                if (!rpc.IsNotification)
                {
                    try
                    {
                        await this.messageHandler.WriteAsync(result.ToJson(), this.disposeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (Exception exception)
                    {
                        var e = new JsonRpcDisconnectedEventArgs(string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcResult, exception.GetType().Name, exception.Message),
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
                    if (this.resultDispatcherMap.TryGetValue(rpc.Id, out data))
                    {
                        this.resultDispatcherMap.Remove(rpc.Id);
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
    }
}
