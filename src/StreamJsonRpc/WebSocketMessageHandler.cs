// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if WEBSOCKETS

namespace StreamJsonRpc
{
    using System;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// A message handler for the <see cref="JsonRpc"/> class
    /// that uses <see cref="System.Net.WebSockets.WebSocket"/> as the transport.
    /// </summary>
    public class WebSocketMessageHandler : DelimitedMessageHandler
    {
        private readonly ArraySegment<byte> readBuffer;
        private MemoryStream readBufferStream;
        private StreamReader readBufferReader;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class.
        /// </summary>
        /// <param name="webSocket">
        /// The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// This will <em>not</em> be automatically disposed of with this <see cref="WebSocketMessageHandler"/>.
        /// </param>
        /// <param name="bufferSize">
        /// The size of the buffer to use for reading JSON-RPC messages.
        /// Messages which exceed this size will be handled properly but may require multiple I/O operations.
        /// </param>
        public WebSocketMessageHandler(WebSocket webSocket, int bufferSize = 4096)
            : base(Stream.Null, Stream.Null, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            Requires.NotNull(webSocket, nameof(webSocket));
            Requires.Range(bufferSize > 0, nameof(bufferSize));

            this.WebSocket = webSocket;
            this.readBuffer = new ArraySegment<byte>(new byte[bufferSize]);
        }

        /// <summary>
        /// Gets the <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// </summary>
        public WebSocket WebSocket { get; }

        /// <inheritdoc />
        protected override async ValueTask<JToken> ReadCoreAsync(CancellationToken cancellationToken)
        {
            if (this.readBufferStream == null)
            {
                this.readBufferStream = new MemoryStream(this.readBuffer.Array.Length);
            }
            else
            {
                this.readBufferStream.SetLength(0);
            }

            if (this.readBufferReader == null || this.readBufferReader.CurrentEncoding != this.Encoding)
            {
                this.readBufferReader = new StreamReader(this.readBufferStream, this.Encoding);
            }
            else
            {
                this.readBufferReader.DiscardBufferedData();
            }

            WebSocketReceiveResult result;
            do
            {
                result = await this.WebSocket.ReceiveAsync(this.readBuffer, cancellationToken).ConfigureAwait(false);
                if (result.CloseStatus.HasValue)
                {
                    await this.WebSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                this.readBufferStream.Write(this.readBuffer.Array, this.readBuffer.Offset, this.readBuffer.Count);
            }
            while (!result.EndOfMessage);

            this.readBufferStream.Position = 0;
            var readBufferJsonReader = new JsonTextReader(this.readBufferReader);
            return JToken.ReadFrom(readBufferJsonReader);
        }

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        /// <inheritdoc />
        protected override ValueTask WriteCoreAsync(JToken content, Encoding contentEncoding, CancellationToken cancellationToken)
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
        {
            Requires.NotNull(content, nameof(content));
            Requires.NotNull(contentEncoding, nameof(contentEncoding));

            MemoryStream sendingContentBufferStream = this.Serialize(content, contentEncoding);
            var bufferSegment = new ArraySegment<byte>(sendingContentBufferStream.GetBuffer(), 0, (int)sendingContentBufferStream.Length);
            return new ValueTask(this.WebSocket.SendAsync(bufferSegment, WebSocketMessageType.Text, true, cancellationToken));
        }
    }
}

#endif
