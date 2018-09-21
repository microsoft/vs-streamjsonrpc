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
        private CountingWriterWrapper<byte> writerWrapper;

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

            readResult = await this.ReadAtLeastAsync(length, allowEmpty: false, cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> content = readResult.Buffer.Slice(0, length);
            JsonRpcMessage message = this.formatter.Deserialize(content);
            this.Reader.AdvanceTo(content.End);
            return message;
        }

        /// <inheritdoc/>
        protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            if (this.writerWrapper == null)
            {
                this.writerWrapper = new CountingWriterWrapper<byte>(this.Writer);
            }

            // Reserve a 4 byte header for the content length.
            Span<byte> lengthBuffer = this.Writer.GetSpan(sizeof(int));
            this.Writer.Advance(4);

            // Write out the actual message content, counting all written bytes.
            this.writerWrapper.Reset();
            this.formatter.Serialize(this.writerWrapper, content);

            // Now go back and fill in the header with the actual content length.
            Utilities.Write(lengthBuffer, this.writerWrapper.ElementCount);
        }

        /// <summary>
        /// An <see cref="IBufferWriter{T}"/> that allows its owner to track how many elements are actually written.
        /// </summary>
        /// <typeparam name="T">The type of element written.</typeparam>
        private class CountingWriterWrapper<T> : IBufferWriter<T>
        {
            /// <summary>
            /// The writer to forward all write calls to.
            /// </summary>
            private readonly IBufferWriter<T> inner;

            /// <summary>
            /// Initializes a new instance of the <see cref="CountingWriterWrapper{T}"/> class.
            /// </summary>
            /// <param name="inner">The writer to forward all write calls to.</param>
            internal CountingWriterWrapper(IBufferWriter<T> inner)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            /// <summary>
            /// Gets the number of elements written since the last call to <see cref="Reset"/>.
            /// </summary>
            internal int ElementCount { get; private set; }

            /// <inheritdoc />
            public void Advance(int count)
            {
                this.ElementCount += count;
                this.inner.Advance(count);
            }

            /// <inheritdoc />
            public Memory<T> GetMemory(int sizeHint = 0) => this.inner.GetMemory(sizeHint);

            /// <inheritdoc />
            public Span<T> GetSpan(int sizeHint = 0) => this.inner.GetSpan(sizeHint);

            /// <summary>
            /// Restarts the <see cref="ElementCount"/> value to 0.
            /// </summary>
            internal void Reset() => this.ElementCount = 0;
        }
    }
}
