// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Class containing useful methods to help on the implementation of message formatters.
    /// </summary>
    public class MessageFormatterHelper
    {
        /// <summary>
        /// Dictionary used to map progress id token to its corresponding <see cref="ProgressParamInformation" /> instance containing the progress object and the necessary fields to report the results.
        /// </summary>
        public readonly Dictionary<long, ProgressParamInformation> progressMap = new Dictionary<long, ProgressParamInformation>();

        /// <summary>
        /// Special method name for progress notification.
        /// </summary>
        internal const string ProgressRequestSpecialMethod = "$/progress";

        /// <summary>
        /// Object used to lock the access to <see cref="requestProgressMap"/> and <see cref="progressMap"/>.
        /// </summary>
        internal readonly object progressLock = new object();

        /// <summary>
        /// Dictionary used to map the outbound request id to their progress id token so that the progress objects are cleaned after getting the final response.
        /// </summary>
        internal readonly Dictionary<long, long> requestProgressMap = new Dictionary<long, long>();

        /// <summary>
        /// Incrementable number to assign as token for the progress objects.
        /// </summary>
        internal long nextProgressId;

        /// <summary>
        /// Stores the id of the request currently being serialized so the converter can use it to create the request-progress map.
        /// </summary>
        internal long? requestIdBeingSerialized;

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

            return objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IProgress<>));
        }

        /// <summary>
        /// Saves a <see cref="ProgressParamInformation"/> instance obtained from the given <see cref="object"/> into <see cref="progressMap"/>.
        /// </summary>
        /// <param name="value">The object which should implement <see cref="IProgress{T}"/> to create the <see cref="ProgressParamInformation"/> instance.</param>
        /// <returns>The assigned <see cref="long"/> progres ID.</returns>
        public long AddProgressObjectToMap(object value)
        {
            Requires.NotNull(value, nameof(value));

            if (this.requestIdBeingSerialized == null)
            {
                throw new NotSupportedException("IProgress<T> objects should not be part of any response or notification.");
            }

            lock (this.progressLock)
            {
                long progressId = this.nextProgressId++;
                this.requestProgressMap.Add(this.requestIdBeingSerialized.Value, progressId);

                this.progressMap.Add(progressId, new ProgressParamInformation(value));

                return progressId;
            }
        }

        /// <summary>
        /// Class used to keep relevant information of an object that implements <see cref="IProgress{T}"/>.
        /// </summary>
        public class ProgressParamInformation
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ProgressParamInformation"/> class.
            /// </summary>
            /// <param name="progressObject">The object implementing <see cref="IProgress{T}"/>.</param>
            public ProgressParamInformation(object progressObject)
            {
                Requires.NotNull(progressObject, nameof(progressObject));

                Type iProgressOfTType = MessageFormatterHelper.FindIProgressOfT(progressObject.GetType());

                Verify.Operation(iProgressOfTType != null, Resources.FindIProgressOfTError);

                this.ValueType = iProgressOfTType.GenericTypeArguments[0];
                this.ReportMethod = iProgressOfTType.GetRuntimeMethod(nameof(IProgress<int>.Report), new Type[] { this.ValueType });
                this.ProgressObject = progressObject;
            }

            /// <summary>
            /// The actual <see cref="Type"/> reported by <see cref="IProgress{T}"/>.
            /// </summary>
            public Type ValueType { get; }

            /// <summary>
            /// The <see cref="MethodInfo"/> of <see cref="IProgress{T}.Report(T)"/>.
            /// </summary>
            public MethodInfo ReportMethod { get; }

            /// <summary>
            /// The instance of the object implementing <see cref="IProgress{T}"/>.
            /// </summary>
            public object ProgressObject { get; }
        }

        /// <summary>
        /// Class that implements <see cref="IProgress{T}"/> and sends <see cref="ProgressRequestSpecialMethod"/> notification when reporting.
        /// </summary>
        public class JsonProgress<T> : IProgress<T>
        {
            private readonly JsonRpc rpc;
            private readonly long? token;

            /// <summary>
            /// Initializes a new instance of the <see cref="JsonProgress{T}"/> class.
            /// </summary>
            /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> norification.</param>
            /// <param name="token">The <see cref="long"/> progress token used to obtain the <see cref="ProgressParamInformation"/> intance from <see cref="progressMap"/>.</param>
            public JsonProgress(JsonRpc rpc, long? token)
            {
                this.rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
                this.token = token ?? throw new ArgumentNullException(nameof(token));
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="JsonProgress{T}"/> class.
            /// </summary>
            /// <param name="rpc">The <see cref="JsonRpc"/> instance used to send the <see cref="ProgressRequestSpecialMethod"/> norification.</param>
            /// <param name="token">The <see cref="JToken"/> progress token used to obtain the <see cref="ProgressParamInformation"/> intance from <see cref="progressMap"/>.</param>
            public JsonProgress(JsonRpc rpc, JToken token)
                : this(rpc, token.Value<long>())
            {
            }

            /// <summary>
            /// Send a <see cref="ProgressRequestSpecialMethod"/> norification using the stored <see cref="JsonRpc"/> instance.
            /// </summary>
            /// <param name="value">The <see cref="T"/> typed value that will be send in the notification to be reported by the original <see cref="IProgress{T}"/> instance.</param>
            public void Report(T value)
            {
                this.rpc.NotifyAsync(ProgressRequestSpecialMethod, this.token, value).Forget();
            }
        }
    }
}
