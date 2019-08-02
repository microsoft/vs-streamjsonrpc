// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipelines;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// A minimal header for each message that simply declares content length.
    /// </summary>
    /// <remarks>
    /// The length is expressed as a big endian, 4 byte integer.
    /// </remarks>
    public class LengthHeaderMessageHandler : PipeMessageHandler
    {
        /// <summary>
        /// The formatter to use for message serialization.
        /// </summary>
        private readonly IJsonRpcMessageFormatter formatter;

        /// <summary>
        /// A wrapper to use for the <see cref="PipeMessageHandler.Writer"/> when we need to count bytes written.
        /// </summary>
        private PrefixingBufferWriter<byte> prefixingWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="LengthHeaderMessageHandler"/> class.
        /// </summary>
        /// <param name="pipe">The reader and writer to use for receiving/transmitting messages.</param>
        /// <param name="formatter">The formatter to use for message serialization.</param>
        public LengthHeaderMessageHandler(IDuplexPipe pipe, IJsonRpcMessageFormatter formatter)
            : base(pipe, formatter)
        {
            Requires.NotNull(formatter, nameof(formatter));
            this.formatter = formatter;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LengthHeaderMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The writer to use for transmitting messages.</param>
        /// <param name="reader">The reader to use for receiving messages.</param>
        /// <param name="formatter">The formatter to use for message serialization.</param>
        public LengthHeaderMessageHandler(PipeWriter writer, PipeReader reader, IJsonRpcMessageFormatter formatter)
            : base(writer, reader, formatter)
        {
            Requires.NotNull(formatter, nameof(formatter));
            this.formatter = formatter;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LengthHeaderMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream to use for transmitting messages.</param>
        /// <param name="receivingStream">The stream to use for receiving messages.</param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        public LengthHeaderMessageHandler(Stream sendingStream, Stream receivingStream, IJsonRpcMessageFormatter formatter)
            : base(sendingStream, receivingStream, formatter)
        {
            Requires.NotNull(formatter, nameof(formatter));
            this.formatter = formatter;
        }

        /// <inheritdoc/>
        protected override async ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken)
        {
            var readResult = await this.ReadAtLeastAsync(4, allowEmpty: true, cancellationToken).ConfigureAwait(false);
            if (readResult.Buffer.Length == 0)
            {
                return null;
            }

            ReadOnlySequence<byte> lengthBuffer = readResult.Buffer.Slice(0, 4);
            int length = Utilities.ReadInt32BE(lengthBuffer);
            this.Reader.AdvanceTo(lengthBuffer.End);

            return await this.DeserializeMessageAsync(length, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            if (this.prefixingWriter == null)
            {
                this.prefixingWriter = new PrefixingBufferWriter<byte>(this.Writer, sizeof(int));
            }

            // Write out the actual message content, counting all written bytes.
            this.formatter.Serialize(this.prefixingWriter, content);

            // Now go back and fill in the header with the actual content length.
            Utilities.Write(this.prefixingWriter.Prefix.Span, checked((int)this.prefixingWriter.Length));
            this.prefixingWriter.Commit();
        }
    }
}
