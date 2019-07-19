
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
    /// Class containing useful method to help on the implementation of message formatters.
    /// </summary>
    public class MessageFormatterHelper
    {
        /// <summary>
        /// Special method name for progress notification.
        /// </summary>
        protected const string ProgressRequestSpecialMethod = "$/progress";

        /// <summary>
        /// Object used to lock the acces to <see cref="requestProgressMap"/> and <see cref="progressMap"/>.
        /// </summary>
        protected readonly object progressLock = new object();

        /// <summary>
        /// Dictionary used to map the request id to their progress id token so that the progress objects are cleaned after getting the final response.
        /// </summary>
        protected readonly Dictionary<long, long> requestProgressMap = new Dictionary<long, long>();

        /// <summary>
        /// Dictionary used to map progress id token to its corresponding ProgressParamInformation instance containing the progress object and the necessary fields to report the results.
        /// </summary>
        protected readonly Dictionary<long, ProgressParamInformation> progressMap = new Dictionary<long, ProgressParamInformation>();

        /// <summary>
        /// Incrementable number to assing as token for the progress objects.
        /// </summary>
        protected long nextProgressId;

        /// <summary>
        /// Stores the id of the request currently being serialized so the converter can use it to create the request-progress map.
        /// </summary>
        protected long? requestIdBeingSerialized;

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

        protected long AddProgressObjectToMap(object value)
        {
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

        protected class ProgressParamInformation
        {
            public ProgressParamInformation(object progressObject)
            {
                Requires.NotNull(progressObject, nameof(progressObject));

                Type iProgressOfTType = MessageFormatterHelper.FindIProgressOfT(progressObject.GetType());

                Verify.Operation(iProgressOfTType != null, Resources.FindIProgressOfTError);

                this.ValueType = iProgressOfTType.GenericTypeArguments[0];
                this.ReportMethod = iProgressOfTType.GetRuntimeMethod(nameof(IProgress<int>.Report), new Type[] { this.ValueType });
                this.ProgressObject = progressObject;
            }

            public Type ValueType { get; }

            public MethodInfo ReportMethod { get; }

            public object ProgressObject { get; }
        }

        protected class JsonProgress<T> : IProgress<T>
        {
            private readonly JsonRpc rpc;
            private readonly long? token;

            public JsonProgress(JsonRpc rpc, long? token)
            {
                this.rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
                this.token = token ?? throw new ArgumentNullException(nameof(token));
            }

            public JsonProgress(JsonRpc rpc, JToken token)
                : this(rpc, token.Value<long>())
            {
            }

            public void Report(T value)
            {
                this.rpc.NotifyAsync(ProgressRequestSpecialMethod, this.token, value).Forget();
            }
        }
    }
}
