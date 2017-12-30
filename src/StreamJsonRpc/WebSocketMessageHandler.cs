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

    /// <summary>
    /// A message handler for the <see cref="JsonRpc"/> class
    /// that uses <see cref="System.Net.WebSockets.WebSocket"/> as the transport.
    /// </summary>
    public class WebSocketMessageHandler : DelimitedMessageHandler
    {
        private readonly ArraySegment<byte> readBuffer;
        private readonly byte[] writeBuffer;
        private Decoder readDecoder;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class.
        /// </summary>
        /// <param name="webSocket">The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.</param>
        /// <param name="bufferSize">
        /// The size of the buffer to use for reading JSON-RPC messages.
        /// Messages which exceed this size will be handled properly but may require multiple I/O operations.
        /// </param>
        public WebSocketMessageHandler(WebSocket webSocket, int bufferSize = 4096)
            : base(Stream.Null, Stream.Null, Encoding.UTF8)
        {
            Requires.NotNull(webSocket, nameof(webSocket));
            Requires.Range(bufferSize > 0, nameof(bufferSize));

            this.WebSocket = webSocket;
            this.readBuffer = new ArraySegment<byte>(new byte[bufferSize]);
            this.writeBuffer = new byte[bufferSize];
        }

        /// <summary>
        /// Gets the <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// </summary>
        public WebSocket WebSocket { get; }

        /// <inheritdoc />
        protected async override Task<string> ReadCoreAsync(CancellationToken cancellationToken)
        {
            WebSocketReceiveResult result = await this.WebSocket.ReceiveAsync(this.readBuffer, cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                // fast path: the entire message fit within the buffer.
                return this.Encoding.GetString(this.readBuffer.Array, 0, result.Count);
            }
            else
            {
                // The message exceeds the size of our buffer.
                if (this.readDecoder == null)
                {
                    this.readDecoder = this.Encoding.GetDecoder();
                }

                var jsonBuilder = new StringBuilder();
                char[] decodedChars = new char[this.readBuffer.Array.Length];
                void DecodeInput()
                {
                    int decodedCharsCount = this.readDecoder.GetChars(this.readBuffer.Array, 0, result.Count, decodedChars, 0, result.EndOfMessage);
                    jsonBuilder.Append(decodedChars, 0, decodedCharsCount);
                }

                DecodeInput();
                while (!result.EndOfMessage)
                {
                    result = await this.WebSocket.ReceiveAsync(this.readBuffer, cancellationToken).ConfigureAwait(false);
                    DecodeInput();
                }

                return jsonBuilder.ToString();
            }
        }

        /// <inheritdoc />
        protected async override Task WriteCoreAsync(string content, Encoding contentEncoding, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));
            Requires.NotNull(contentEncoding, nameof(contentEncoding));

            if (contentEncoding.GetByteCount(content) <= this.writeBuffer.Length)
            {
                // Fast path: send the whole message as a single chunk.
                int bytesWritten = contentEncoding.GetBytes(content, 0, content.Length, this.writeBuffer, 0);
                var bufferSegment = new ArraySegment<byte>(this.writeBuffer, 0, bytesWritten);
                await this.WebSocket.SendAsync(bufferSegment, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var encoder = contentEncoding.GetEncoder();
                bool completed;
                int bytesUsed;
                int charsLeftToConvert = content.Length;
                while (charsLeftToConvert > 0)
                {
                    unsafe
                    {
                        fixed (byte* writeBuffer = this.writeBuffer)
                        fixed (char* pContent = content)
                        {
                            char* pStart = pContent + content.Length - charsLeftToConvert;
                            encoder.Convert(pStart, charsLeftToConvert, writeBuffer, this.writeBuffer.Length, false, out int charsUsed, out bytesUsed, out completed);
                            charsLeftToConvert -= charsUsed;
                        }
                    }

                    var bufferSegment = new ArraySegment<byte>(this.writeBuffer, 0, bytesUsed);
                    await this.WebSocket.SendAsync(bufferSegment, WebSocketMessageType.Text, completed, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.WebSocket.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

#endif
