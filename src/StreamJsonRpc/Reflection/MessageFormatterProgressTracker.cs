// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

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
        /// Dictionary to record the calculation made in <see cref="FindIProgressOfT"/> to obtain the <see cref="IProgress{T}"/> type from a given <see cref="Type"/>.
        /// </summary>
        private static readonly Dictionary<Type, Type> IProgressOfTTypeMap = new Dictionary<Type, Type>();

        /// <summary>
        /// Dictionary used to map the outbound request id to their progress id token so that the progress objects are cleaned after getting the final response.
        /// </summary>
        private readonly Dictionary<RequestId, long> requestProgressMap = new Dictionary<RequestId, long>();

        /// <summary>
        /// Dictionary used to map progress id token to its corresponding <see cref="ProgressParamInformation" /> instance containing the progress object and the necessary fields to report the results.
        /// </summary>
        private readonly Dictionary<long, ProgressParamInformation> progressMap = new Dictionary<long, ProgressParamInformation>();

        /// <summary>
        /// Object used to lock the access to <see cref="requestProgressMap"/> and <see cref="progressMap"/>.
        /// </summary>
        private readonly object progressLock = new object();

        /// <summary>
        /// Gets or sets the the next id value to assign as token for the progress objects.
        /// </summary>
        private long nextProgressId;

        /// <summary>
        /// Gets or Sets the id of the request currently being serialized so the converter can use it to create the request-progress map.
        /// </summary>
        public RequestId RequestIdBeingSerialized { get; set; }

        /// <summary>
        /// Converts given <see cref="Type"/> to its <see cref="IProgress{T}"/> type.
        /// </summary>
        /// <param name="objectType">The type which may implement <see cref="IProgress{T}"/>.</param>
        /// <returns>The <see cref="IProgress{T}"/> from given <see cref="Type"/> object, or <c>null</c>  if no such interface was found in the given <paramref name="objectType" />.</returns>
        public static Type FindIProgressOfT(Type objectType)
        {
            Requires.NotNull(objectType, nameof(objectType));

            if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
            {
                return objectType;
            }

            Type? iProgressOfTType;
            lock (IProgressOfTTypeMap)
            {
                if (!IProgressOfTTypeMap.TryGetValue(objectType, out iProgressOfTType))
                {
                    iProgressOfTType = objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IProgress<>));
                    IProgressOfTTypeMap.Add(objectType, iProgressOfTType);
                }
            }

            return iProgressOfTType;
        }

        /// <summary>
        /// Checks if a given <see cref="Type"/> implements <see cref="IProgress{T}"/>.
        /// </summary>
        /// <param name="objectType">The type which may implement <see cref="IProgress{T}"/>.</param>
        /// <returns>true if given <see cref="Type"/> implements <see cref="IProgress{T}"/>; otherwise, false.</returns>
        public static bool IsSupportedProgressType(Type objectType)
        {
            return FindIProgressOfT(objectType) != null;
        }

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
                long progressId = this.nextProgressId++;
                this.requestProgressMap.Add(this.RequestIdBeingSerialized, progressId);

                this.progressMap.Add(progressId, new ProgressParamInformation(value));

                return progressId;
            }
        }

        /// <summary>
        /// Call this method when a response is received to clear the objects associated with the request and avoid a memory leak.
        /// </summary>
        /// <param name="requestId">The id of the request whose associated objects need to be cleared.</param>
        public void OnResponseReceived(RequestId requestId)
        {
            lock (this.progressLock)
            {
                if (this.requestProgressMap.TryGetValue(requestId, out long progressId))
                {
                    this.requestProgressMap.Remove(requestId);
                    this.progressMap.Remove(progressId);
                }
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

        /// <summary>
        /// Creates a new instance of <see cref="IProgress{T}"/> to use on the receiving end of an RPC call.
        /// </summary>
        /// <typeparam name="T">The type of the value to be reported by <see cref="IProgress{T}"/>.</typeparam>
        /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> notification.</param>
        /// <param name="token">The token used to obtain the <see cref="ProgressParamInformation"/> instance from <see cref="progressMap"/>.</param>
        public IProgress<T> CreateProgress<T>(JsonRpc rpc, object token) => new JsonProgress<T>(rpc, token);

        /// <summary>
        /// Creates a new instance of <see cref="IProgress{T}"/> to use on the receiving end of an RPC call.
        /// </summary>
        /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> notification.</param>
        /// <param name="token">The token used to obtain the <see cref="ProgressParamInformation"/> instance from <see cref="progressMap"/>.</param>
        /// <param name="valueType">The type that the <see cref="IProgress{T}"/> intance will report.</param>
        public object CreateProgress(JsonRpc rpc, object token, Type valueType)
        {
            Requires.NotNull(rpc, nameof(rpc));
            Requires.NotNull(token, nameof(token));
            Requires.NotNull(valueType, nameof(valueType));

            Type progressType = typeof(JsonProgress<>).MakeGenericType(valueType.GenericTypeArguments[0]);
            return Activator.CreateInstance(progressType, new object[] { rpc, token })!;
        }

        /// <summary>
        /// Class used to keep relevant information of an object that implements <see cref="IProgress{T}"/>.
        /// </summary>
        public class ProgressParamInformation
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
            public ProgressParamInformation(object progressObject)
            {
                Requires.NotNull(progressObject, nameof(progressObject));

                Type iProgressOfTType = FindIProgressOfT(progressObject.GetType());

                Verify.Operation(iProgressOfTType != null, Resources.FindIProgressOfTError);

                this.ValueType = iProgressOfTType.GenericTypeArguments[0];
                this.reportMethod = iProgressOfTType.GetRuntimeMethod(nameof(IProgress<int>.Report), new Type[] { this.ValueType })!;
                this.progressObject = progressObject;
            }

            /// <summary>
            /// Gets the actual <see cref="Type"/> reported by <see cref="IProgress{T}"/>.
            /// </summary>
            public Type ValueType { get; }

            /// <summary>
            /// Invokes <see cref="reportMethod"/> using the given typed value.
            /// </summary>
            /// <param name="typedValue">The value to be reported.</param>
            public void InvokeReport(object? typedValue)
            {
                this.reportMethod.Invoke(this.progressObject, new object?[] { typedValue });
            }
        }

        /// <summary>
        /// Class that implements <see cref="IProgress{T}"/> and sends <see cref="ProgressRequestSpecialMethod"/> notification when reporting.
        /// </summary>
        private class JsonProgress<T> : IProgress<T>
        {
            private readonly JsonRpc rpc;
            private readonly object token;

            /// <summary>
            /// Initializes a new instance of the <see cref="JsonProgress{T}"/> class.
            /// </summary>
            /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> notification.</param>
            /// <param name="token">The progress token used to obtain the <see cref="ProgressParamInformation"/> instance from <see cref="progressMap"/>.</param>
            public JsonProgress(JsonRpc rpc, object token)
            {
                this.rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
                this.token = token ?? throw new ArgumentNullException(nameof(token));
            }

            /// <summary>
            /// Send a <see cref="ProgressRequestSpecialMethod"/> norification using the stored <see cref="JsonRpc"/> instance.
            /// </summary>
            /// <param name="value">The typed value that will be send in the notification to be reported by the original <see cref="IProgress{T}"/> instance.</param>
            public void Report(T value)
            {
                this.rpc.NotifyAsync(ProgressRequestSpecialMethod, this.token, value).Forget();
            }
        }
    }
}
