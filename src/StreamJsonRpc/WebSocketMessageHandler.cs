// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.IO;
    using System.Net.WebSockets;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// A message handler for the <see cref="JsonRpc"/> class
    /// that uses <see cref="System.Net.WebSockets.WebSocket"/> as the transport.
    /// </summary>
    public class WebSocketMessageHandler : MessageHandlerBase
    {
        private readonly int sizeHint;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class
        /// that uses the <see cref="JsonMessageFormatter"/> to serialize messages as textual JSON.
        /// </summary>
        /// <param name="webSocket">
        /// The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// This will <em>not</em> be automatically disposed of with this <see cref="WebSocketMessageHandler"/>.
        /// </param>
        public WebSocketMessageHandler(WebSocket webSocket)
            : this(webSocket, new JsonMessageFormatter())
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

        /// <inheritdoc />
        protected override async ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken)
        {
            using (var contentSequenceBuilder = new Sequence<byte>())
            {
#if NETCOREAPP2_1
                ValueWebSocketReceiveResult result;
#else
                WebSocketReceiveResult result;
#endif
                do
                {
                    var memory = contentSequenceBuilder.GetMemory(this.sizeHint);
#if NETCOREAPP2_1
                    result = await this.WebSocket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
                    contentSequenceBuilder.Advance(result.Count);
#else
                    ArrayPool<byte> pool = ArrayPool<byte>.Shared;
                    byte[] segment = pool.Rent(this.sizeHint);
                    try
                    {
                        result = await this.WebSocket.ReceiveAsync(new ArraySegment<byte>(segment), cancellationToken).ConfigureAwait(false);
                        contentSequenceBuilder.Write(segment.AsSpan(0, result.Count));
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

                return contentSequenceBuilder.AsReadOnlySequence.Length > 0 ? this.Formatter.Deserialize(contentSequenceBuilder) : null;
            }
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix

        /// <inheritdoc />
        protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));

            using (var contentSequenceBuilder = new Sequence<byte>())
            {
                var messageType = this.Formatter is IJsonRpcMessageTextFormatter ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                this.Formatter.Serialize(contentSequenceBuilder, content);
                cancellationToken.ThrowIfCancellationRequested();
                int bytesCopied = 0;
                ReadOnlySequence<byte> contentSequence = contentSequenceBuilder.AsReadOnlySequence;
                foreach (ReadOnlyMemory<byte> memory in contentSequence)
                {
                    bool endOfMessage = bytesCopied + memory.Length == contentSequence.Length;
#if NETCOREAPP2_1
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

#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
    }
}
