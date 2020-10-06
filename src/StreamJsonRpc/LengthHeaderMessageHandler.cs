// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Buffers;
    using System.IO;
    using System.IO.Pipelines;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

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
        /// The <see cref="IBufferWriter{T}"/> sent to the <see cref="formatter"/> to write the message.
        /// </summary>
        private readonly Sequence<byte> contentSequenceBuilder = new Sequence<byte>(ArrayPool<byte>.Shared);

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
        public LengthHeaderMessageHandler(PipeWriter? writer, PipeReader? reader, IJsonRpcMessageFormatter formatter)
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
        public LengthHeaderMessageHandler(Stream? sendingStream, Stream? receivingStream, IJsonRpcMessageFormatter formatter)
            : base(sendingStream, receivingStream, formatter)
        {
            Requires.NotNull(formatter, nameof(formatter));
            this.formatter = formatter;
        }

        /// <inheritdoc/>
        protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
        {
            Assumes.NotNull(this.Reader);
            ReadResult readResult = await this.ReadAtLeastAsync(4, allowEmpty: true, cancellationToken).ConfigureAwait(false);
            if (readResult.Buffer.Length == 0)
            {
                return null;
            }

            ReadOnlySequence<byte> lengthBuffer = readResult.Buffer.Slice(0, 4);
            int length = Utilities.ReadInt32BE(lengthBuffer);
            this.Reader.AdvanceTo(lengthBuffer.End);

            JsonRpcMessage message = await this.DeserializeMessageAsync(length, cancellationToken).ConfigureAwait(false);

            if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
            {
                JsonRpcEventSource.Instance.HandlerReceived(length);
            }

            return message;
        }

        /// <inheritdoc/>
        protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Assumes.NotNull(this.Writer);

            try
            {
                // Write out the actual message content.
                this.formatter.Serialize(this.contentSequenceBuilder, content);
                ReadOnlySequence<byte> contentSequence = this.contentSequenceBuilder.AsReadOnlySequence;

                // Some formatters (e.g. MessagePackFormatter) needs the encoded form in order to produce JSON for tracing.
                // Other formatters (e.g. JsonMessageFormatter) would prefer to do its own tracing while it still has a JToken.
                // We only help the formatters that need the byte-encoded form here. The rest can do it themselves.
                if (this.Formatter is IJsonRpcFormatterTracingCallbacks tracer)
                {
                    tracer.OnSerializationComplete(content, contentSequence);
                }

                // Now go back and fill in the header with the actual content length.
                Utilities.Write(this.Writer.GetSpan(sizeof(int)), checked((int)contentSequence.Length));
                this.Writer.Advance(sizeof(int));
                contentSequence.CopyTo(this.Writer);

                if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
                {
                    JsonRpcEventSource.Instance.HandlerTransmitted(contentSequence.Length);
                }
            }
            finally
            {
                this.contentSequenceBuilder.Reset();
            }
        }
    }
}
