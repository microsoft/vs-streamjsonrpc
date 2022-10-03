﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.VisualStudio.Threading;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Class containing useful methods to help message formatters implement support for <see cref="IProgress{T}"/>.
/// </summary>
public class MessageFormatterProgressTracker
{
    /// <summary>
    /// Special method name for progress notification.
    /// </summary>
    public const string ProgressRequestSpecialMethod = "$/progress";

    /// <summary>
    /// Dictionary used to map the outbound request id to their progress info so that the progress objects are cleaned after getting the final response.
    /// </summary>
    private readonly Dictionary<RequestId, ImmutableList<ProgressParamInformation>> requestProgressMap = new Dictionary<RequestId, ImmutableList<ProgressParamInformation>>();

    /// <summary>
    /// Dictionary used to map progress id token to its corresponding <see cref="ProgressParamInformation" /> instance containing the progress object and the necessary fields to report the results.
    /// </summary>
    private readonly Dictionary<long, ProgressParamInformation> progressMap = new Dictionary<long, ProgressParamInformation>();

    /// <summary>
    /// Object used to lock the access to <see cref="requestProgressMap"/> and <see cref="progressMap"/>.
    /// </summary>
    private readonly object progressLock = new object();

    /// <summary>
    /// State from the formatter that owns this tracker.
    /// </summary>
    private readonly IJsonRpcFormatterState formatterState;

    /// <summary>
    /// Gets or sets the the next id value to assign as token for the progress objects.
    /// </summary>
    private long nextProgressId;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageFormatterProgressTracker"/> class.
    /// </summary>
    /// <param name="jsonRpc">The <see cref="JsonRpc"/> object that ultimately owns this tracker.</param>
    /// <param name="formatterState">The formatter that owns this tracker.</param>
    public MessageFormatterProgressTracker(JsonRpc jsonRpc, IJsonRpcFormatterState formatterState)
    {
        Requires.NotNull(jsonRpc, nameof(jsonRpc));
        Requires.NotNull(formatterState, nameof(formatterState));

        this.formatterState = formatterState;

        IJsonRpcFormatterCallbacks callbacks = jsonRpc;
        callbacks.RequestTransmissionAborted += (s, e) => this.CleanUpResources(e.RequestId);
        callbacks.ResponseReceived += (s, e) => this.CleanUpResources(e.RequestId);
    }

    /// <summary>
    /// Gets the id of the request currently being serialized so the converter can use it to create the request-progress map.
    /// </summary>
    private RequestId RequestIdBeingSerialized => this.formatterState.SerializingRequest ? this.formatterState.SerializingMessageWithId : default;

    /// <summary>
    /// Converts given <see cref="Type"/> to its <see cref="IProgress{T}"/> type.
    /// </summary>
    /// <param name="objectType">The type which may implement <see cref="IProgress{T}"/>.</param>
    /// <returns>The <see cref="IProgress{T}"/> from given <see cref="Type"/> object, or <see langword="null"/>  if no such interface was found in the given <paramref name="objectType" />.</returns>
    public static Type? FindIProgressOfT(Type objectType) => TrackerHelpers<IProgress<int>>.FindInterfaceImplementedBy(objectType);

    /// <summary>
    /// Checks if a given <see cref="Type"/> implements <see cref="IProgress{T}"/>.
    /// </summary>
    /// <param name="objectType">The type which may implement <see cref="IProgress{T}"/>.</param>
    /// <returns>true if given <see cref="Type"/> implements <see cref="IProgress{T}"/>; otherwise, false.</returns>
    public static bool IsSupportedProgressType(Type objectType) => TrackerHelpers<IProgress<int>>.CanSerialize(objectType);

    /// <summary>
    /// Gets a <see cref="long"/> type token to use as replacement of an <see cref="object"/> implementing <see cref="IProgress{T}"/> in the JSON message.
    /// </summary>
    /// <param name="value">The object which should implement <see cref="IProgress{T}"/>.</param>
    /// <returns>The assigned <see cref="long"/> typed token.</returns>
    public long GetTokenForProgress(object value)
    {
        Requires.NotNull(value, nameof(value));

        if (this.RequestIdBeingSerialized.IsEmpty)
        {
            throw new NotSupportedException(Resources.MarshaledObjectInResponseOrNotificationError);
        }

        lock (this.progressLock)
        {
            // Check whether we're being asked to tokenize a Progress<T> object for a second time (this can happen due to message logging).
            if (this.requestProgressMap.TryGetValue(this.RequestIdBeingSerialized, out ImmutableList<ProgressParamInformation>? progressInfos))
            {
                foreach (ProgressParamInformation info in progressInfos)
                {
                    if (info.Contains(value))
                    {
                        return info.Token;
                    }
                }
            }
            else
            {
                progressInfos = ImmutableList<ProgressParamInformation>.Empty;
            }

            long progressToken = this.nextProgressId++;
            var progressInfo = new ProgressParamInformation(value, progressToken);

            progressInfos = progressInfos.Add(progressInfo);
            this.requestProgressMap[this.RequestIdBeingSerialized] = progressInfos;

            this.progressMap.Add(progressToken, progressInfo);

            return progressToken;
        }
    }

    /// <summary>
    /// Gets the <see cref="ProgressParamInformation"/> object associated with the given progress id.
    /// </summary>
    /// <param name="progressId">The key to obtain the <see cref="ProgressParamInformation"/> object from <see cref="progressMap"/>.</param>
    /// <param name="valueType">Output parameter to store the obtained <see cref="ProgressParamInformation"/> object.</param>
    /// <returns>true if the <see cref="ProgressParamInformation"/> object was found with the specified key; otherwise, false.</returns>
    public bool TryGetProgressObject(long progressId, [NotNullWhen(true)] out ProgressParamInformation? valueType)
    {
        lock (this.progressLock)
        {
            if (this.progressMap.TryGetValue(progressId, out ProgressParamInformation? progressInfo))
            {
                valueType = progressInfo;
                return true;
            }

            valueType = null;
            return false;
        }
    }

    /// <inheritdoc cref="CreateProgress(JsonRpc, object, Type, bool)"/>
    /// <remarks>
    /// This overload creates an <see cref="IProgress{T}"/> that does <em>not</em> use named arguments in its notifications.
    /// </remarks>
    public IProgress<T> CreateProgress<T>(JsonRpc rpc, object token) => this.CreateProgress<T>(rpc, token, clientRequiresNamedArguments: false);

    /// <inheritdoc cref="CreateProgress(JsonRpc, object, Type, bool)"/>
    /// <typeparam name="T">The type of the value to be reported by <see cref="IProgress{T}"/>.</typeparam>
    public IProgress<T> CreateProgress<T>(JsonRpc rpc, object token, bool clientRequiresNamedArguments) => new JsonProgress<T>(rpc, token, clientRequiresNamedArguments);

    /// <inheritdoc cref="CreateProgress(JsonRpc, object, Type, bool)"/>
    /// <remarks>
    /// This overload creates an <see cref="IProgress{T}"/> that does <em>not</em> use named arguments in its notifications.
    /// </remarks>
    public object CreateProgress(JsonRpc rpc, object token, Type valueType) => this.CreateProgress(rpc, token, valueType, clientRequiresNamedArguments: false);

    /// <summary>
    /// Creates a new instance of <see cref="IProgress{T}"/> to use on the receiving end of an RPC call.
    /// </summary>
    /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> notification.</param>
    /// <param name="token">The token used to obtain the <see cref="ProgressParamInformation"/> instance from <see cref="progressMap"/>.</param>
    /// <param name="valueType">A generic type whose first generic type argument is to serve as the type argument for the created <see cref="IProgress{T}"/>.</param>
    /// <param name="clientRequiresNamedArguments"><see langword="true"/> to issue $/progress notifications using named args; <see langword="false"/> to use positional arguments.</param>
#pragma warning disable CA1822 // Mark members as static
    public object CreateProgress(JsonRpc rpc, object token, Type valueType, bool clientRequiresNamedArguments)
#pragma warning restore CA1822 // Mark members as static
    {
        Requires.NotNull(rpc, nameof(rpc));
        Requires.NotNull(token, nameof(token));
        Requires.NotNull(valueType, nameof(valueType));

        Type progressType = typeof(JsonProgress<>).MakeGenericType(valueType.GenericTypeArguments[0]);
        return Activator.CreateInstance(progressType, new object[] { rpc, token, clientRequiresNamedArguments })!;
    }

    private void CleanUpResources(RequestId requestId)
    {
        lock (this.progressLock)
        {
            if (this.requestProgressMap.TryGetValue(requestId, out ImmutableList<ProgressParamInformation>? progressInfos))
            {
                this.requestProgressMap.Remove(requestId);
                foreach (ProgressParamInformation progressInfo in progressInfos)
                {
                    this.progressMap.Remove(progressInfo.Token);
                }
            }
        }
    }

    /// <summary>
    /// Class used to keep relevant information of an object that implements <see cref="IProgress{T}"/>.
    /// </summary>
#pragma warning disable CA1034 // Nested types should not be visible
    public class ProgressParamInformation
#pragma warning restore CA1034 // Nested types should not be visible
    {
        /// <summary>
        /// Gets the <see cref="MethodInfo"/> of <see cref="IProgress{T}.Report(T)"/>.
        /// </summary>
        private readonly MethodInfo reportMethod;

        /// <summary>
        /// Gets the instance of the object implementing <see cref="IProgress{T}"/>.
        /// </summary>
        private readonly object progressObject;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressParamInformation"/> class.
        /// </summary>
        /// <param name="progressObject">The object implementing <see cref="IProgress{T}"/>.</param>
        /// <param name="token">The token associated with this progress object.</param>
        internal ProgressParamInformation(object progressObject, long token)
        {
            Requires.NotNull(progressObject, nameof(progressObject));

            Type? iProgressOfTType = FindIProgressOfT(progressObject.GetType());

            Verify.Operation(iProgressOfTType is not null, Resources.FindIProgressOfTError);

            this.ValueType = iProgressOfTType.GenericTypeArguments[0];
            this.reportMethod = iProgressOfTType.GetRuntimeMethod(nameof(IProgress<int>.Report), new Type[] { this.ValueType })!;
            this.progressObject = progressObject;
            this.Token = token;
        }

        /// <summary>
        /// Gets the actual <see cref="Type"/> reported by <see cref="IProgress{T}"/>.
        /// </summary>
        public Type ValueType { get; }

        /// <summary>
        /// Gets the token associated with this progress object.
        /// </summary>
        public long Token { get; }

        /// <summary>
        /// Invokes <see cref="reportMethod"/> using the given typed value.
        /// </summary>
        /// <param name="typedValue">The value to be reported.</param>
        public void InvokeReport(object? typedValue)
        {
            this.reportMethod.Invoke(this.progressObject, new object?[] { typedValue });
        }

        internal bool Contains(object progressObject) => this.progressObject == progressObject;
    }

    /// <summary>
    /// Class that implements <see cref="IProgress{T}"/> and sends <see cref="ProgressRequestSpecialMethod"/> notification when reporting.
    /// </summary>
    private class JsonProgress<T> : IProgress<T>
    {
        private readonly JsonRpc rpc;
        private readonly object token;
        private readonly bool useNamedArguments;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonProgress{T}"/> class.
        /// </summary>
        /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> notification.</param>
        /// <param name="token">The progress token used to obtain the <see cref="ProgressParamInformation"/> instance from <see cref="progressMap"/>.</param>
        /// <param name="useNamedArguments"><see langword="true"/> to use named arguments; <see langword="false"/> to use positional arguments.</param>
        public JsonProgress(JsonRpc rpc, object token, bool useNamedArguments)
        {
            this.rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
            this.token = token ?? throw new ArgumentNullException(nameof(token));
            this.useNamedArguments = useNamedArguments;
        }

        /// <summary>
        /// Send a <see cref="ProgressRequestSpecialMethod"/> norification using the stored <see cref="JsonRpc"/> instance.
        /// </summary>
        /// <param name="value">The typed value that will be send in the notification to be reported by the original <see cref="IProgress{T}"/> instance.</param>
        public void Report(T value)
        {
            Task notifyTask;
            if (this.useNamedArguments)
            {
                var arguments = new Dictionary<string, object?>
                {
                    { "token",  this.token },
                    { "value", value },
                };
                var argumentDeclaredTypes = new Dictionary<string, Type>
                {
                    { "token", this.token.GetType() },
                    { "value", typeof(T) },
                };
                notifyTask = this.rpc.NotifyWithParameterObjectAsync(ProgressRequestSpecialMethod, arguments, argumentDeclaredTypes);
            }
            else
            {
                var arguments = new object?[] { this.token, value };
                var argumentDeclaredTypes = new Type[] { this.token.GetType(), typeof(T) };
                notifyTask = this.rpc.NotifyAsync(ProgressRequestSpecialMethod, arguments, argumentDeclaredTypes);
            }

            notifyTask.ContinueWith(
                (t, s) => ((JsonRpc)s!).TraceSource.TraceEvent(System.Diagnostics.TraceEventType.Error, (int)JsonRpc.TraceEvents.ProgressNotificationError, "Failed to send progress update. {0}", t.Exception!.InnerException ?? t.Exception),
                this.rpc,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default).Forget();
        }
    }
}
