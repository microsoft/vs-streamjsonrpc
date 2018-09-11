// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Uses Newtonsoft.Json serialization to translate <see cref="JToken"/> used by <see cref="IJsonMessageHandler"/>
    /// into <see cref="JsonRpcMessage"/> objects.
    /// </summary>
    internal class JsonMessageHandler : IMessageHandler, IDisposableObservable
    {
        /// <summary>
        /// The key into an <see cref="Exception.Data"/> dictionary whose value may be a <see cref="JToken"/> that failed deserialization.
        /// </summary>
        internal const string ExceptionDataKey = "JToken";

        /// <summary>
        /// The underlying <see cref="JToken"/>-based message handler.
        /// </summary>
        private readonly IJsonMessageHandler innerHandler;

        /// <summary>
        /// Backing field for the <see cref="IsDisposed"/> property.
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonMessageHandler"/> class.
        /// </summary>
        /// <param name="jsonMessageHandler">The <see cref="IJsonMessageHandler"/> to wrap.</param>
        public JsonMessageHandler(IJsonMessageHandler jsonMessageHandler)
        {
            this.innerHandler = jsonMessageHandler ?? throw new ArgumentNullException(nameof(jsonMessageHandler));

            this.MessageJsonSerializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            };
            this.MessageJsonDeserializerSettings = new JsonSerializerSettings
            {
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                Converters = this.MessageJsonSerializerSettings.Converters,
            };
        }

        /// <inheritdoc/>
        public bool CanRead => this.innerHandler.CanRead;

        /// <inheritdoc/>
        public bool CanWrite => this.innerHandler.CanWrite;

        /// <inheritdoc/>
        public bool IsDisposed => (this.innerHandler as IDisposableObservable)?.IsDisposed ?? this.isDisposed;

        private JsonSerializerSettings MessageJsonDeserializerSettings { get; }

        private JsonSerializerSettings MessageJsonSerializerSettings { get; }

        /// <inheritdoc/>
        public async ValueTask<JsonRpcMessage> ReadAsync(CancellationToken cancellationToken)
        {
            JToken json = await this.innerHandler.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return null;
            }

            try
            {
                return JsonRpcMessage.FromJson(json, this.MessageJsonDeserializerSettings);
            }
            catch (JsonException exception)
            {
                var serializationException = new JsonSerializationException($"Unable to deserialize {nameof(JsonRpcMessage)}.", exception);
                serializationException.Data[ExceptionDataKey] = json;
                throw serializationException;
            }
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        /// <inheritdoc/>
        public ValueTask WriteAsync(JsonRpcMessage jsonRpcMessage, CancellationToken cancellationToken)
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonSerializer serializer = JsonSerializer.Create(this.MessageJsonSerializerSettings);
            JObject json = (JObject)JToken.FromObject(jsonRpcMessage, serializer);
            return this.innerHandler.WriteAsync(json, cancellationToken);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.isDisposed = true;
            (this.innerHandler as IDisposable)?.Dispose();
        }
    }
}
