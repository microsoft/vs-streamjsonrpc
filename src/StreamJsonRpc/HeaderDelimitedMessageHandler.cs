// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Buffers.Text;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipelines;
    using System.Net.Http.Headers;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// Adds headers before each text message transmitted over a stream.
    /// </summary>
    /// <remarks>
    /// This is based on the language server protocol spec:
    /// https://github.com/Microsoft/language-server-protocol/blob/master/protocol.md#base-protocol.
    /// </remarks>
    public class HeaderDelimitedMessageHandler : PipeMessageHandler
    {
        private const string ContentLengthHeaderNameText = "Content-Length";
        private const string ContentTypeHeaderNameText = "Content-Type";
        private const string DefaultSubType = "jsonrpc";

        /// <summary>
        /// The default encoding to use when writing content,
        /// and to assume as the encoding when reading content
        /// that doesn't have a header identifying its encoding.
        /// </summary>
        private static readonly Encoding DefaultContentEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// The encoding to use when writing/reading headers.
        /// </summary>
        /// <remarks>
        /// Although the spec dictates using ASCII encoding, that's equivalent to UTF8
        /// for the characters we expect to be sending and receiving,
        /// and portable profiles don't have ASCII available.
        /// Also note that when writing we use the encoding set by this field,
        /// but when reading, we have highly optimized code that hard-codes the assumption
        /// that each character is one byte.
        /// </remarks>
        private static readonly Encoding HeaderEncoding = Encoding.UTF8;

        private static readonly byte[] ContentLengthHeaderName = HeaderEncoding.GetBytes(ContentLengthHeaderNameText);
        private static readonly byte[] HeaderKeyValueDelimiter = HeaderEncoding.GetBytes(": ");
        private static readonly byte[] ContentTypeHeaderName = HeaderEncoding.GetBytes(ContentTypeHeaderNameText);
        private static readonly byte[] CrlfBytes = HeaderEncoding.GetBytes("\r\n");

        /// <summary>
        /// The <see cref="IBufferWriter{T}"/> sent to the <see cref="TextFormatter"/> to write the message.
        /// </summary>
        private readonly Sequence<byte> contentSequenceBuilder = new Sequence<byte>(ArrayPool<byte>.Shared);

        /// <summary>
        /// Backing field for <see cref="SubType"/>.
        /// </summary>
        private string subType = DefaultSubType;

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The writer to use for transmitting messages.</param>
        /// <param name="reader">The reader to use for receiving messages.</param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        public HeaderDelimitedMessageHandler(PipeWriter? writer, PipeReader? reader, IJsonRpcMessageFormatter formatter)
            : base(writer, reader, formatter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="pipe">The duplex pipe to use for exchanging messages.</param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        public HeaderDelimitedMessageHandler(IDuplexPipe pipe, IJsonRpcMessageFormatter formatter)
            : this(Requires.NotNull(pipe, nameof(pipe)).Output, pipe.Input, formatter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="duplexStream">The stream to use for exchanging messages.</param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        public HeaderDelimitedMessageHandler(Stream duplexStream, IJsonRpcMessageFormatter formatter)
            : this(duplexStream, duplexStream, formatter)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="duplexStream">The stream to use for transmitting and receiving messages.</param>
        public HeaderDelimitedMessageHandler(Stream duplexStream)
            : this(duplexStream, duplexStream)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream to use for transmitting messages.</param>
        /// <param name="receivingStream">The stream to use for receiving messages.</param>
        public HeaderDelimitedMessageHandler(Stream? sendingStream, Stream? receivingStream)
#pragma warning disable CA2000 // Dispose objects before losing scope
            : this(sendingStream, receivingStream, new JsonMessageFormatter())
#pragma warning restore CA2000 // Dispose objects before losing scope
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream to use for transmitting messages.</param>
        /// <param name="receivingStream">The stream to use for receiving messages.</param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        public HeaderDelimitedMessageHandler(Stream? sendingStream, Stream? receivingStream, IJsonRpcMessageFormatter formatter)
            : base(sendingStream, receivingStream, formatter)
        {
        }

        private enum HeaderParseState
        {
            Name,
            Value,
            FieldDelimiter,
            EndOfHeader,
            Terminate,
        }

        /// <summary>
        /// Gets or sets the value to use as the subtype in the Content-Type header (e.g. "application/SUBTYPE").
        /// </summary>
        /// <value>The default value is "jsonrpc".</value>
        public string SubType
        {
            get => this.subType;
            set
            {
                Requires.NotNull(value, nameof(value));
                Requires.Argument(value.Length < 100, nameof(value), Resources.HeaderValueTooLarge);
                this.subType = value;
            }
        }

        /// <summary>
        /// Gets or sets the encoding to use for transmitted messages.
        /// </summary>
        /// <exception cref="NotSupportedException">Thrown if the <see cref="MessageHandlerBase.Formatter"/> in use does not implement <see cref="IJsonRpcMessageTextFormatter"/>.</exception>
        public Encoding Encoding
        {
            get => this.TextFormatter.Encoding;
            set => this.TextFormatter.Encoding = value;
        }

        /// <summary>
        /// Gets the formatter to use to serialize <see cref="JsonRpcMessage"/> instances as text.
        /// Throws if the formatter is not a text-based formatter.
        /// </summary>
        private IJsonRpcMessageTextFormatter TextFormatter => this.Formatter as IJsonRpcMessageTextFormatter ?? throw this.ThrowNoTextEncoder();

        /// <inheritdoc />
        protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
        {
            (int? ContentLength, Encoding? ContentEncoding)? headers = await this.ReadHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (!headers.HasValue)
            {
                // end of stream reached before the next message started.
                return null;
            }

            if (!headers.Value.ContentLength.HasValue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new BadRpcHeaderException("No Content-Length header detected.");
            }

            int contentLength = headers.Value.ContentLength.Value;
            JsonRpcMessage message = await this.DeserializeMessageAsync(contentLength, headers.Value.ContentEncoding, DefaultContentEncoding, cancellationToken).ConfigureAwait(false);

            if (JsonRpcEventSource.Instance.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
            {
                JsonRpcEventSource.Instance.HandlerReceived(contentLength);
            }

            return message;
        }

        /// <inheritdoc />
        protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Assumes.NotNull(this.Writer);
            unsafe int WriteHeaderText(string value, Span<byte> memory)
            {
                fixed (char* pValue = &MemoryMarshal.GetReference(value.AsSpan()))
                {
                    fixed (byte* pMemory = &MemoryMarshal.GetReference(memory))
                    {
                        return HeaderEncoding.GetBytes(pValue, value.Length, pMemory, memory.Length);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Encoding contentEncoding = this.Encoding;
            try
            {
                this.Formatter.Serialize(this.contentSequenceBuilder, content);
                ReadOnlySequence<byte> contentSequence = this.contentSequenceBuilder.AsReadOnlySequence;

                // Some formatters (e.g. MessagePackFormatter) needs the encoded form in order to produce JSON for tracing.
                // Other formatters (e.g. JsonMessageFormatter) would prefer to do its own tracing while it still has a JToken.
                // We only help the formatters that need the byte-encoded form here. The rest can do it themselves.
                if (this.Formatter is IJsonRpcFormatterTracingCallbacks tracer)
                {
                    tracer.OnSerializationComplete(content, this.contentSequenceBuilder);
                }

                Memory<byte> headerMemory = this.Writer.GetMemory(1024);
                int bytesWritten = 0;

                // Transmit the Content-Length header.
                ContentLengthHeaderName.CopyTo(headerMemory.Slice(bytesWritten));
                bytesWritten += ContentLengthHeaderName.Length;
                HeaderKeyValueDelimiter.CopyTo(headerMemory.Slice(bytesWritten));
                bytesWritten += HeaderKeyValueDelimiter.Length;

                Assumes.True(Utf8Formatter.TryFormat(contentSequence.Length, headerMemory.Span.Slice(bytesWritten), out int formattedBytes));
                bytesWritten += formattedBytes;

                CrlfBytes.CopyTo(headerMemory.Slice(bytesWritten));
                bytesWritten += CrlfBytes.Length;

                // Transmit the Content-Type header, but only when using a non-default encoding.
                // We suppress it when it is the default both for smaller messages and to avoid
                // having to load System.Net.Http on the receiving end in order to parse it.
                if (DefaultContentEncoding.WebName != contentEncoding.WebName || this.SubType != DefaultSubType)
                {
                    ContentTypeHeaderName.CopyTo(headerMemory.Slice(bytesWritten));
                    bytesWritten += ContentTypeHeaderName.Length;
                    HeaderKeyValueDelimiter.CopyTo(headerMemory.Slice(bytesWritten));
                    bytesWritten += HeaderKeyValueDelimiter.Length;

                    bytesWritten += WriteHeaderText("application/", headerMemory.Slice(bytesWritten).Span);
                    bytesWritten += WriteHeaderText(this.SubType, headerMemory.Slice(bytesWritten).Span);
                    bytesWritten += WriteHeaderText("; charset=", headerMemory.Slice(bytesWritten).Span);
                    bytesWritten += WriteHeaderText(contentEncoding.WebName, headerMemory.Slice(bytesWritten).Span);

                    CrlfBytes.CopyTo(headerMemory.Slice(bytesWritten));
                    bytesWritten += CrlfBytes.Length;
                }

                // Terminate the headers.
                CrlfBytes.CopyTo(headerMemory.Slice(bytesWritten));
                bytesWritten += CrlfBytes.Length;
                this.Writer.Advance(bytesWritten);
                bytesWritten = 0;

                // Transmit the content itself.
                Memory<byte> contentMemory = this.Writer.GetMemory((int)contentSequence.Length);
                contentSequence.CopyTo(contentMemory.Span);
                this.Writer.Advance((int)contentSequence.Length);

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

        /// <summary>
        /// Extracts the content encoding from a Content-Type header.
        /// </summary>
        /// <param name="contentTypeValue">The value of the Content-Type header.</param>
        /// <returns>The Encoding, if the header specified one; otherwise <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)] // keep System.Net.Http dependency in its own method to avoid loading it if there is no such header.
        private static unsafe Encoding? ParseEncodingFromContentTypeHeader(ReadOnlySequence<byte> contentTypeValue)
        {
            // Protect against blowing the stack since we're using stackalloc below.
            if (contentTypeValue.Length > 200)
            {
                throw new BadRpcHeaderException("Content-Type header value length exceeds maximum allowed.");
            }

            try
            {
                Span<byte> stackSpan = stackalloc byte[(int)contentTypeValue.Length];
                ReadOnlySpan<byte> contentTypeValueSpan = stackSpan;
                if (contentTypeValue.IsSingleSegment)
                {
                    contentTypeValueSpan = contentTypeValue.First.Span;
                }
                else
                {
                    contentTypeValue.CopyTo(stackSpan);
                }

                contentTypeValueSpan = Trim(contentTypeValueSpan);

                string contentTypeAsText;
                fixed (byte* contentTypeValuePointer = &MemoryMarshal.GetReference(contentTypeValueSpan))
                {
                    contentTypeAsText = HeaderEncoding.GetString(contentTypeValuePointer, contentTypeValueSpan.Length);
                }

                var mediaType = MediaTypeHeaderValue.Parse(contentTypeAsText);
                if (mediaType.CharSet != null)
                {
                    // The common language server protocol accpets 'utf8' as a valid charset due to an early bug.  To maintain backwards compatibility, 'utf8' will be
                    // accepted here so StreamJsonRpc can be used to support remote language servers following common language protocol.
                    if (mediaType.CharSet.Equals("utf8", StringComparison.OrdinalIgnoreCase))
                    {
                        return Encoding.UTF8;
                    }

                    Encoding contentEncoding = Encoding.GetEncoding(mediaType.CharSet);
                    if (contentEncoding == null)
                    {
                        throw new BadRpcHeaderException($"Unrecognized charset value: '{mediaType.CharSet}'");
                    }

                    return contentEncoding;
                }

                return null;
            }
            catch (FormatException ex)
            {
                throw new BadRpcHeaderException(ex.Message, ex);
            }
        }

        private static bool IsLastFourBytesCrlfCrlf(byte[] buffer, int lastIndex)
        {
            const byte cr = (byte)'\r';
            const byte lf = (byte)'\n';
            return lastIndex >= 4
                && buffer[lastIndex - 4] == cr
                && buffer[lastIndex - 3] == lf
                && buffer[lastIndex - 2] == cr
                && buffer[lastIndex - 1] == lf;
        }

        private static int GetContentLength(ReadOnlySequence<byte> contentLengthValue)
        {
            // Ensure the length is reasonable so we don't blow the stack if we execute the stackalloc path.
            if (contentLengthValue.Length > 20)
            {
                throw new BadRpcHeaderException("Content-Length header's value has a length that exceeds the maximum allowed.");
            }

            Span<byte> stackSpan = stackalloc byte[(int)contentLengthValue.Length];
            ReadOnlySpan<byte> contentLengthSpan = stackSpan;
            if (contentLengthValue.IsSingleSegment)
            {
                contentLengthSpan = contentLengthValue.First.Span;
            }
            else
            {
                contentLengthValue.CopyTo(stackSpan);
            }

            contentLengthSpan = Trim(contentLengthSpan);

            if (!Utf8Parser.TryParse(contentLengthSpan, out int contentLength, out int bytesConsumed) || bytesConsumed < contentLengthSpan.Length)
            {
                throw new BadRpcHeaderException("Unable to parse Content-Length header value as an integer.");
            }

            return contentLength;
        }

        private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span) => TrimStart(TrimEnd(span));

        private static ReadOnlySpan<byte> TrimStart(ReadOnlySpan<byte> span)
        {
            while (span.Length > 0 && char.IsWhiteSpace((char)span[0]))
            {
                span = span.Slice(1);
            }

            return span;
        }

        private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> span)
        {
            while (span.Length > 0 && char.IsWhiteSpace((char)span[span.Length - 1]))
            {
                span = span.Slice(0, span.Length - 1);
            }

            return span;
        }

        private async ValueTask<(int? ContentLength, Encoding? ContentEncoding)?> ReadHeadersAsync(CancellationToken cancellationToken)
        {
            bool IsHeaderName(ReadOnlySequence<byte> buffer, ReadOnlySpan<byte> asciiHeaderName)
            {
                if (asciiHeaderName.Length != buffer.Length)
                {
                    return false;
                }

                foreach (ReadOnlyMemory<byte> segment in buffer)
                {
                    if (!asciiHeaderName.Slice(0, segment.Length).SequenceEqual(segment.Span))
                    {
                        return false;
                    }

                    asciiHeaderName = asciiHeaderName.Slice(segment.Length);
                }

                return true;
            }

            Assumes.NotNull(this.Reader);
            int? contentLengthHeaderValue = null;
            Encoding? contentEncoding = null;

            while (true)
            {
                ReadResult readResult = await this.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (readResult.Buffer.Length == 0 && readResult.IsCompleted)
                {
                    return default; // remote end disconnected at a reasonable place.
                }

                SequencePosition? lf = readResult.Buffer.PositionOf((byte)'\n');
                if (!lf.HasValue)
                {
                    if (readResult.IsCompleted)
                    {
                        throw new EndOfStreamException();
                    }

                    // Indicate that we can't find what we're looking for and read again.
                    this.Reader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
                    continue;
                }

                ReadOnlySequence<byte> line = readResult.Buffer.Slice(0, lf.Value);

                // Verify the line ends with an \r (that precedes the \n we already found)
                SequencePosition? cr = line.PositionOf((byte)'\r');
                if (!cr.HasValue || !line.GetPosition(1, cr.Value).Equals(lf))
                {
                    throw new BadRpcHeaderException("Header does not end with expected \r\n character sequence: " + HeaderEncoding.GetString(line.ToArray()));
                }

                // Trim off the \r now that we confirmed it was there.
                line = line.Slice(0, line.Length - 1);

                if (line.Length > 0)
                {
                    SequencePosition? colon = line.PositionOf((byte)':');
                    if (!colon.HasValue)
                    {
                        throw new BadRpcHeaderException("Colon not found in header.");
                    }

                    ReadOnlySequence<byte> headerNameBytes = line.Slice(0, colon.Value);
                    ReadOnlySequence<byte> headerValueBytes = line.Slice(line.GetPosition(1, colon.Value));

                    if (IsHeaderName(headerNameBytes, ContentLengthHeaderName))
                    {
                        contentLengthHeaderValue = GetContentLength(headerValueBytes);
                    }
                    else if (IsHeaderName(headerNameBytes, ContentTypeHeaderName))
                    {
                        contentEncoding = ParseEncodingFromContentTypeHeader(headerValueBytes);
                    }
                }

                // Advance to the next line.
                this.Reader.AdvanceTo(readResult.Buffer.GetPosition(1, lf.Value));

                if (line.Length == 0)
                {
                    // We found the empty line that constitutes the end of the HTTP headers.
                    break;
                }
            }

            return (contentLengthHeaderValue, contentEncoding);
        }
    }
}
