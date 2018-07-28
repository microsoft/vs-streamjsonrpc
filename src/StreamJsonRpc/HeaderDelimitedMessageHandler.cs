// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.Http.Headers;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// Adds headers before each text message transmitted over a stream.
    /// </summary>
    /// <remarks>
    /// This is based on the language server protocol spec:
    /// https://github.com/Microsoft/language-server-protocol/blob/master/protocol.md#base-protocol
    /// </remarks>
    public class HeaderDelimitedMessageHandler : DelimitedMessageHandler
    {
        /// <summary>
        /// The maximum supported size of a single element in the header.
        /// </summary>
        private const int MaxHeaderElementSize = 1024;
        private const string ContentLengthHeaderNameText = "Content-Length";
        private const string ContentTypeHeaderNameText = "Content-Type";
        private const string DefaultSubType = "jsonrpc";

        /// <summary>
        /// The default encoding to use when writing content,
        /// and to assume as the encoding when reading content
        /// that doesn't have a header identifying its encoding.
        /// </summary>
        private static readonly Encoding DefaultContentEncoding = Encoding.UTF8;

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

        private readonly byte[] sendingHeaderBuffer = new byte[MaxHeaderElementSize];

        private readonly byte[] receivingBuffer = new byte[MaxHeaderElementSize];

        private readonly Dictionary<string, string> receivingHeaders = new Dictionary<string, string>(4);

        /// <summary>
        /// Initializes a new instance of the <see cref="HeaderDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        public HeaderDelimitedMessageHandler(Stream sendingStream, Stream receivingStream)
            : base(sendingStream, receivingStream != null ? new ReadBufferingStream(receivingStream, MaxHeaderElementSize) : null, DefaultContentEncoding)
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
        public string SubType { get; set; } = DefaultSubType;

        private new ReadBufferingStream ReceivingStream => (ReadBufferingStream)base.ReceivingStream;

        /// <inheritdoc />
        protected override async Task<string> ReadCoreAsync(CancellationToken cancellationToken)
        {
            this.receivingHeaders.Clear();
            int headerBytesLength = 0;
            var state = HeaderParseState.Name;
            string headerName = null;
            do
            {
                if (this.receivingBuffer.Length - headerBytesLength == 0)
                {
                    throw new BadRpcHeaderException(Resources.HeaderValueTooLarge);
                }

                if (this.ReceivingStream.IsBufferEmpty)
                {
                    await this.ReceivingStream.FillBufferAsync(cancellationToken).ConfigureAwait(false);
                }

                int justRead = this.ReceivingStream.ReadByte();
                if (justRead == -1)
                {
                    return null; // remote end disconnected
                }

                this.receivingBuffer[headerBytesLength] = (byte)justRead;
                headerBytesLength++;
                char lastCharRead = (char)justRead;
                switch (state)
                {
                    case HeaderParseState.Name:
                        if (lastCharRead == ':')
                        {
                            headerName = HeaderEncoding.GetString(this.receivingBuffer, index: 0, count: headerBytesLength - 1);
                            state = HeaderParseState.Value;
                            headerBytesLength = 0;
                        }
                        else if (lastCharRead == '\r' && headerBytesLength == 1)
                        {
                            state = HeaderParseState.EndOfHeader;
                            headerBytesLength = 0;
                        }
                        else if (lastCharRead == '\r' || lastCharRead == '\n')
                        {
                            ThrowUnexpectedToken(lastCharRead);
                        }

                        break;
                    case HeaderParseState.Value:
                        if (lastCharRead == ' ')
                        {
                            --headerBytesLength;
                        }

                        // spec mandates \r always precedes \n
                        if (lastCharRead == '\r')
                        {
                            string value = HeaderEncoding.GetString(this.receivingBuffer, index: 0, count: headerBytesLength - 1);
                            this.receivingHeaders[headerName] = value;
                            headerName = null;
                            state = HeaderParseState.FieldDelimiter;
                            headerBytesLength = 0;
                        }

                        break;
                    case HeaderParseState.FieldDelimiter:
                        ThrowIfNotExpectedToken(lastCharRead, '\n');
                        state = HeaderParseState.Name;
                        headerBytesLength = 0;
                        break;
                    case HeaderParseState.EndOfHeader:
                        ThrowIfNotExpectedToken(lastCharRead, '\n');
                        state = HeaderParseState.Terminate;
                        headerBytesLength = 0;
                        break;
                }
            }
            while (state != HeaderParseState.Terminate);

            string contentLengthAsText = this.receivingHeaders[ContentLengthHeaderNameText];
            if (!int.TryParse(contentLengthAsText, out int contentLength))
            {
                throw new BadRpcHeaderException(string.Format(CultureInfo.CurrentCulture, Resources.HeaderContentLengthNotParseable, contentLengthAsText));
            }

            Encoding contentEncoding = this.Encoding;
            if (this.receivingHeaders.TryGetValue(ContentTypeHeaderNameText, out string contentTypeAsText))
            {
                contentEncoding = ParseEncodingFromContentTypeHeader(contentTypeAsText) ?? contentEncoding;
            }

            byte[] contentBuffer = contentLength <= this.receivingBuffer.Length
                ? this.receivingBuffer
                : new byte[contentLength];

            int bytesRead = 0;
            while (bytesRead < contentLength)
            {
                int bytesJustRead = await this.ReceivingStream.ReadAsync(contentBuffer, bytesRead, contentLength - bytesRead, cancellationToken).ConfigureAwait(false);
                if (bytesJustRead == 0)
                {
                    // Early termination of stream.
                    return null;
                }

                bytesRead += bytesJustRead;
            }

            return contentEncoding.GetString(contentBuffer, 0, contentLength);
        }

        /// <inheritdoc />
        protected override async Task WriteCoreAsync(string content, Encoding contentEncoding, CancellationToken cancellationToken)
        {
            var sendingBufferStream = new MemoryStream(MaxHeaderElementSize);

            // Understand the content we need to send in terms of bytes and length.
            byte[] contentBytes = contentEncoding.GetBytes(content);
            string contentBytesLength = contentBytes.Length.ToString(CultureInfo.InvariantCulture);

            // Transmit the Content-Length header.
#pragma warning disable VSTHRD103 // Call async methods when in an async method
            sendingBufferStream.Write(ContentLengthHeaderName, 0, ContentLengthHeaderName.Length);
            sendingBufferStream.Write(HeaderKeyValueDelimiter, 0, HeaderKeyValueDelimiter.Length);
            int headerValueBytesLength = HeaderEncoding.GetBytes(contentBytesLength, 0, contentBytesLength.Length, this.sendingHeaderBuffer, 0);
            sendingBufferStream.Write(this.sendingHeaderBuffer, 0, headerValueBytesLength);
            sendingBufferStream.Write(CrlfBytes, 0, CrlfBytes.Length);

            // Transmit the Content-Type header, but only when using a non-default encoding.
            // We suppress it when it is the default both for smaller messages and to avoid
            // having to load System.Net.Http on the receiving end in order to parse it.
            if (DefaultContentEncoding.WebName != contentEncoding.WebName || this.SubType != DefaultSubType)
            {
                sendingBufferStream.Write(ContentTypeHeaderName, 0, ContentTypeHeaderName.Length);
                sendingBufferStream.Write(HeaderKeyValueDelimiter, 0, HeaderKeyValueDelimiter.Length);
                var contentTypeHeaderValue = $"application/{this.SubType}; charset={contentEncoding.WebName}";
                headerValueBytesLength = HeaderEncoding.GetBytes(contentTypeHeaderValue, 0, contentTypeHeaderValue.Length, this.sendingHeaderBuffer, 0);
                sendingBufferStream.Write(this.sendingHeaderBuffer, 0, headerValueBytesLength);
                sendingBufferStream.Write(CrlfBytes, 0, CrlfBytes.Length);
            }

            // Terminate the headers.
            sendingBufferStream.Write(CrlfBytes, 0, CrlfBytes.Length);
#pragma warning restore VSTHRD103 // Call async methods when in an async method

            // Either write both the header and the content, or don't write anything.
            // If we write only the header when the cancellation comes, that would confuse the recieving side
            // and corrupt the data data it reads.
            cancellationToken.ThrowIfCancellationRequested();

            // Transmit the headers.
            // Ignore the cancellation token so we don't write the header without the content.
            sendingBufferStream.Position = 0;
            await sendingBufferStream.CopyToAsync(this.SendingStream, MaxHeaderElementSize).ConfigureAwait(false);

            // Transmit the content itself.
            // Ignore the cancellation token so we don't write the header without the content.
            await this.SendingStream.WriteAsync(contentBytes, 0, contentBytes.Length).ConfigureAwait(false);
        }

        /// <summary>
        /// Extracts the content encoding from a Content-Type header.
        /// </summary>
        /// <param name="contentTypeAsText">The value of the Content-Type header.</param>
        /// <returns>The Encoding, if the header specified one; otherwise <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)] // keep System.Net.Http dependency in its own method to avoid loading it if there is no such header.
        private static Encoding ParseEncodingFromContentTypeHeader(string contentTypeAsText)
        {
            try
            {
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

        private static void ThrowIfNotExpectedToken(char actual, char expected)
        {
            if (actual != expected)
            {
                ThrowUnexpectedToken(actual, expected);
            }
        }

        private static Exception ThrowUnexpectedToken(char actual, char? expected = null)
        {
            throw new BadRpcHeaderException(
                string.Format(CultureInfo.CurrentCulture, Resources.UnexpectedTokenReadingHeader, actual));
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
    }
}
