// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Net.WebSockets;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// A message handler for the <see cref="JsonRpc"/> class
    /// that uses <see cref="System.Net.WebSockets.WebSocket"/> as the transport.
    /// </summary>
    public class WebSocketMessageHandler : MessageHandlerBase, IJsonRpcMessageBufferManager
    {
        private readonly int sizeHint;

        private readonly Sequence<byte> contentSequenceBuilder = new Sequence<byte>();

        private IJsonRpcMessageBufferManager? bufferedMessage;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class
        /// that uses the <see cref="JsonMessageFormatter"/> to serialize messages as textual JSON.
        /// </summary>
        /// <param name="webSocket">
        /// The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// This will <em>not</em> be automatically disposed of with this <see cref="WebSocketMessageHandler"/>.
        /// </param>
        public WebSocketMessageHandler(WebSocket webSocket)
#pragma warning disable CA2000 // Dispose objects before losing scope
            : this(webSocket, new JsonMessageFormatter())
#pragma warning restore CA2000 // Dispose objects before losing scope
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class.
        /// </summary>
        /// <param name="webSocket">
        /// The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// This will <em>not</em> be automatically disposed of with this <see cref="WebSocketMessageHandler"/>.
        /// </param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        /// <param name="sizeHint">
        /// The size of the buffer to use for reading JSON-RPC messages.
        /// Messages which exceed this size will be handled properly but may require multiple I/O operations.
        /// </param>
        public WebSocketMessageHandler(WebSocket webSocket, IJsonRpcMessageFormatter formatter, int sizeHint = 4096)
            : base(formatter)
        {
            Requires.NotNull(webSocket, nameof(webSocket));
            Requires.Range(sizeHint > 0, nameof(sizeHint));

            this.WebSocket = webSocket;
            this.sizeHint = sizeHint;
        }

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <summary>
        /// Gets the <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// </summary>
        public WebSocket WebSocket { get; }

        /// <inheritdoc/>
        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
        {
            if (message is object && this.bufferedMessage == message)
            {
                this.bufferedMessage.DeserializationComplete(message);
                this.bufferedMessage = null;
                this.contentSequenceBuilder.Reset();
            }
        }

        /// <inheritdoc />
        protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
        {
#if NETSTANDARD2_1_OR_GREATER
            ValueWebSocketReceiveResult result;
#else
            WebSocketReceiveResult result;
#endif
            do
            {
#if NETSTANDARD2_1_OR_GREATER
                Memory<byte> memory = this.contentSequenceBuilder.GetMemory(this.sizeHint);
                result = await this.WebSocket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
                this.contentSequenceBuilder.Advance(result.Count);
#else
                ArrayPool<byte> pool = ArrayPool<byte>.Shared;
                byte[] segment = pool.Rent(this.sizeHint);
                try
                {
                    result = await this.WebSocket.ReceiveAsync(new ArraySegment<byte>(segment), cancellationToken).ConfigureAwait(false);
                    this.contentSequenceBuilder.Write(segment.AsSpan(0, result.Count));
                }
                finally
                {
                    pool.Return(segment);
                }
#endif
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await this.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed as requested.", CancellationToken.None).ConfigureAwait(false);
                    return null;
                }
            }
            while (!result.EndOfMessage);

            if (this.contentSequenceBuilder.AsReadOnlySequence.Length > 0)
            {
                JsonRpcMessage message = this.Formatter.Deserialize(this.contentSequenceBuilder);
                this.bufferedMessage = message as IJsonRpcMessageBufferManager;
                if (this.bufferedMessage is null)
                {
                    this.contentSequenceBuilder.Reset();
                }

                return message;
            }
            else
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));

            using (var contentSequenceBuilder = new Sequence<byte>())
            {
                WebSocketMessageType messageType = this.Formatter is IJsonRpcMessageTextFormatter ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                this.Formatter.Serialize(contentSequenceBuilder, content);
                cancellationToken.ThrowIfCancellationRequested();

                // Some formatters (e.g. MessagePackFormatter) needs the encoded form in order to produce JSON for tracing.
                // Other formatters (e.g. JsonMessageFormatter) would prefer to do its own tracing while it still has a JToken.
                // We only help the formatters that need the byte-encoded form here. The rest can do it themselves.
                if (this.Formatter is IJsonRpcFormatterTracingCallbacks tracer)
                {
                    tracer.OnSerializationComplete(content, contentSequenceBuilder);
                }

                int bytesCopied = 0;
                ReadOnlySequence<byte> contentSequence = contentSequenceBuilder.AsReadOnlySequence;
                foreach (ReadOnlyMemory<byte> memory in contentSequence)
                {
                    bool endOfMessage = bytesCopied + memory.Length == contentSequence.Length;
#if NETSTANDARD2_1_OR_GREATER
                    await this.WebSocket.SendAsync(memory, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
#else
                    if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment))
                    {
                        await this.WebSocket.SendAsync(segment, messageType, endOfMessage, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        byte[] array = ArrayPool<byte>.Shared.Rent(memory.Length);
                        try
                        {
                            memory.CopyTo(array);
                            await this.WebSocket.SendAsync(new ArraySegment<byte>(array, 0, memory.Length), messageType, endOfMessage, CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(array);
                        }
                    }
#endif

                    bytesCopied += memory.Length;
                }
            }
        }

        /// <inheritdoc />
        protected override ValueTask FlushAsync(CancellationToken cancellationToken) => default;
    }
}
