// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO.Pipelines;
using Nerdbank.Streams;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// A base class for <see cref="IJsonRpcMessageFormatter"/> implementations
/// that support exotic types.
/// </summary>
public abstract class FormatterBase : IJsonRpcFormatterState, IJsonRpcInstanceContainer, IDisposable
{
    private JsonRpc? rpc;

    /// <summary>
    /// Backing field for the <see cref="Nerdbank.Streams.MultiplexingStream"/> property.
    /// </summary>
    private MultiplexingStream? multiplexingStream;

    /// <summary>
    /// The <see cref="MessageFormatterProgressTracker"/> we use to support <see cref="IProgress{T}"/> method arguments.
    /// </summary>
    private MessageFormatterProgressTracker? formatterProgressTracker;

    /// <summary>
    /// The helper for marshaling pipes as RPC method arguments.
    /// </summary>
    private MessageFormatterDuplexPipeTracker? duplexPipeTracker;

    /// <summary>
    /// The tracker we use to support transmission of <see cref="IAsyncEnumerable{T}"/> types.
    /// </summary>
    private MessageFormatterEnumerableTracker? enumerableTracker;

    /// <summary>
    /// The helper for marshaling <see cref="IRpcMarshaledContext{T}"/> in RPC method arguments or return values.
    /// </summary>
    private MessageFormatterRpcMarshaledContextTracker? rpcMarshaledContextTracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="FormatterBase"/> class.
    /// </summary>
    public FormatterBase()
    {
    }

    /// <inheritdoc  />
    public RequestId SerializingMessageWithId { get; private set; }

    /// <inheritdoc  />
    public RequestId DeserializingMessageWithId { get; private set; }

    /// <inheritdoc  />
    public bool SerializingRequest { get; private set; }

    /// <inheritdoc/>
    JsonRpc IJsonRpcInstanceContainer.Rpc
    {
        set
        {
            Verify.Operation(this.rpc is null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
            if (value is not null)
            {
                this.rpc = value;

                this.formatterProgressTracker = new MessageFormatterProgressTracker(value, this);
                this.enumerableTracker = new MessageFormatterEnumerableTracker(value, this);
                this.duplexPipeTracker = new MessageFormatterDuplexPipeTracker(value, this) { MultiplexingStream = this.MultiplexingStream };
                this.rpcMarshaledContextTracker = new MessageFormatterRpcMarshaledContextTracker(value, this);
            }
        }
    }

    /// <summary>
    /// Gets or sets the <see cref="MultiplexingStream"/> that may be used to establish out of band communication (e.g. marshal <see cref="IDuplexPipe"/> arguments).
    /// </summary>
    public MultiplexingStream? MultiplexingStream
    {
        get => this.multiplexingStream;
        set
        {
            Verify.Operation(this.JsonRpc is null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
            this.multiplexingStream = value;
        }
    }

    /// <summary>
    /// Gets the <see cref="StreamJsonRpc.JsonRpc"/> that is associated with this formatter.
    /// </summary>
    /// <remarks>
    /// This field is used to create the <see cref="IProgress{T}" /> instance that will send the progress notifications when server reports it.
    /// The <see cref="IJsonRpcInstanceContainer.Rpc" /> property helps to ensure that only one <see cref="JsonRpc" /> instance is associated with this formatter.
    /// </remarks>
    protected JsonRpc? JsonRpc => this.rpc;

    /// <summary>
    /// Gets the <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
    /// </summary>
    protected MessageFormatterProgressTracker FormatterProgressTracker
    {
        get
        {
            Assumes.NotNull(this.formatterProgressTracker); // This should have been set in the Rpc property setter.
            return this.formatterProgressTracker;
        }
    }

    /// <summary>
    /// Gets the helper for marshaling pipes as RPC method arguments.
    /// </summary>
    protected MessageFormatterDuplexPipeTracker DuplexPipeTracker
    {
        get
        {
            Assumes.NotNull(this.duplexPipeTracker); // This should have been set in the Rpc property setter.
            return this.duplexPipeTracker;
        }
    }

    /// <summary>
    /// Gets the helper for marshaling <see cref="IAsyncEnumerable{T}"/> in RPC method arguments or return values.
    /// </summary>
    protected MessageFormatterEnumerableTracker EnumerableTracker
    {
        get
        {
            Assumes.NotNull(this.enumerableTracker); // This should have been set in the Rpc property setter.
            return this.enumerableTracker;
        }
    }

    /// <summary>
    /// Gets the helper for marshaling <see cref="IRpcMarshaledContext{T}"/> in RPC method arguments or return values.
    /// </summary>
    private protected MessageFormatterRpcMarshaledContextTracker RpcMarshaledContextTracker
    {
        get
        {
            Assumes.NotNull(this.rpcMarshaledContextTracker); // This should have been set in the Rpc property setter.
            return this.rpcMarshaledContextTracker;
        }
    }

    /// <summary>
    /// Gets the message whose arguments are being deserialized.
    /// </summary>
    private protected JsonRpcMessage? DeserializingMessage { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and native resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> if being disposed; <see langword="false"/> if being finalized.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.duplexPipeTracker?.Dispose();
        }
    }

    /// <summary>
    /// Sets up state to track deserialization of a message.
    /// </summary>
    /// <param name="message">The object being deserialized.</param>
    /// <returns>A value to dispose of when deserialization has completed.</returns>
    protected DeserializationTracking TrackDeserialization(JsonRpcMessage message) => new(this, message);

    /// <summary>
    /// Sets up state to track serialization of a message.
    /// </summary>
    /// <param name="message">The message being serialized.</param>
    /// <returns>A value to dispose of when serialization has completed.</returns>
    protected SerializationTracking TrackSerialization(JsonRpcMessage message) => new(this, message);

    private protected void TryHandleSpecialIncomingMessage(JsonRpcMessage message)
    {
        switch (message)
        {
            case JsonRpcRequest request:
                // If method is $/progress, get the progress instance from the dictionary and call Report
                if (this.JsonRpc is not null && string.Equals(request.Method, MessageFormatterProgressTracker.ProgressRequestSpecialMethod, StringComparison.Ordinal))
                {
                    try
                    {
                        if (request.TryGetArgumentByNameOrIndex("token", 0, typeof(long), out object? tokenObject) && tokenObject is long progressId)
                        {
                            MessageFormatterProgressTracker.ProgressParamInformation? progressInfo = null;
                            if (this.FormatterProgressTracker.TryGetProgressObject(progressId, out progressInfo))
                            {
                                if (request.TryGetArgumentByNameOrIndex("value", 1, progressInfo.ValueType, out object? value))
                                {
                                    progressInfo.InvokeReport(value);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.JsonRpc.TraceSource.TraceData(TraceEventType.Error, (int)JsonRpc.TraceEvents.ProgressNotificationError, ex);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Tracks deserialization of a message.
    /// </summary>
    public struct DeserializationTracking : IDisposable
    {
        private readonly FormatterBase formatter;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeserializationTracking"/> struct.
        /// </summary>
        /// <param name="formatter">The formatter.</param>
        /// <param name="message">The message being deserialized.</param>
        public DeserializationTracking(FormatterBase formatter, JsonRpcMessage message)
        {
            // Deserialization of messages should never occur concurrently for a single instance of a formatter.
            Assumes.True(formatter.DeserializingMessageWithId.IsEmpty);

            this.formatter = formatter;
            this.formatter.DeserializingMessage = message;
            this.formatter.DeserializingMessageWithId = (message as IJsonRpcMessageWithId)?.RequestId ?? default;
        }

        /// <summary>
        /// Clears deserialization state.
        /// </summary>
        public void Dispose()
        {
            this.formatter.DeserializingMessageWithId = default;
            this.formatter.DeserializingMessage = null;
        }
    }

    /// <summary>
    /// Tracks serialization of a message.
    /// </summary>
    public struct SerializationTracking : IDisposable
    {
        private readonly FormatterBase formatter;

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializationTracking"/> struct.
        /// </summary>
        /// <param name="formatter">The formatter.</param>
        /// <param name="message">The message being serialized.</param>
        public SerializationTracking(FormatterBase formatter, JsonRpcMessage message)
        {
            this.formatter = formatter;
            this.formatter.SerializingMessageWithId = (message as IJsonRpcMessageWithId)?.RequestId ?? default;
            this.formatter.SerializingRequest = message is JsonRpcRequest;
        }

        /// <summary>
        /// Clears serialization state.
        /// </summary>
        public void Dispose()
        {
            this.formatter.SerializingMessageWithId = default;
            this.formatter.SerializingRequest = false;
        }
    }
}
