using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;

namespace StreamJsonRpc
{
    /// <summary>
    /// Adds headers before each text message transmitted over a stream.
    /// </summary>
    /// <remarks>
    /// This is based on the language server protocol spec:
    /// https://github.com/Microsoft/language-server-protocol/blob/master/protocol.md#base-protocol
    /// </remarks>
    internal class HeaderDelimitedMessageHandler : IDisposable
    {
        /// <summary>
        /// The maximum supported size of a single element in the header.
        /// </summary>
        private const int MaxHeaderElementSize = 1024;

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

        private const string ContentLengthHeaderNameText = "Content-Length";
        private const string ContentTypeHeaderNameText = "Content-Type";
        private static readonly byte[] ContentLengthHeaderName = HeaderEncoding.GetBytes(ContentLengthHeaderNameText);
        private static readonly byte[] HeaderKeyValueDelimiter = HeaderEncoding.GetBytes(": ");
        private static readonly byte[] ContentTypeHeaderName = HeaderEncoding.GetBytes(ContentTypeHeaderNameText);
        private static readonly byte[] CrlfBytes = HeaderEncoding.GetBytes("\r\n");

        private readonly Stream sendingStream;
        private readonly AsyncSemaphore sendingSemaphore = new AsyncSemaphore(1);
        private readonly byte[] sendingHeaderBuffer = new byte[MaxHeaderElementSize];

        private readonly ReadBufferingStream receivingStream;
        private readonly AsyncSemaphore receivingSemaphore = new AsyncSemaphore(1);
        private readonly byte[] receivingBuffer = new byte[MaxHeaderElementSize];

        private Encoding encoding = Encoding.UTF8;

        internal HeaderDelimitedMessageHandler(Stream sendingStream, Stream receivingStream)
        {
            this.sendingStream = sendingStream;
            this.receivingStream = new ReadBufferingStream(receivingStream, MaxHeaderElementSize);
        }

        private enum HeaderParseState
        {
            Name,
            NameValueDelimiter,
            Value,
            FieldDelimiter,
            EndOfHeader,
            Terminate,
        }

        /// <summary>
        /// Gets or sets the encoding to use for transmitted JSON messages.
        /// </summary>
        public Encoding Encoding
        {
            get
            {
                return this.encoding;
            }

            set
            {
                Requires.NotNull(value, nameof(value));
                this.encoding = value;
            }
        }

        /// <summary>
        /// Gets or sets the value to use as the subtype in the Content-Type header (e.g. "application/SUBTYPE").
        /// </summary>
        public string SubType { get; set; } = "jsonrpc";

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.receivingStream?.Dispose();
                this.sendingStream?.Dispose();
                this.sendingSemaphore.Dispose();
                this.receivingSemaphore.Dispose();
            }
        }

        internal async Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.receivingStream != null, "No receiving stream.");

            using (await this.receivingSemaphore.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                var headers = new Dictionary<string, string>();

                int headerBytesLength = 0;
                var state = HeaderParseState.Name;
                string headerName = null;
                do
                {
                    if (this.receivingBuffer.Length - headerBytesLength == 0)
                    {
                        throw new BadRpcHeaderException(Resources.HeaderValueTooLarge);
                    }

                    if (this.receivingStream.IsBufferEmpty)
                    {
                        await this.receivingStream.FillBufferAsync(cancellationToken).ConfigureAwait(false);
                    }

                    int justRead = this.receivingStream.ReadByte();
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
                                state = HeaderParseState.NameValueDelimiter;
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
                        case HeaderParseState.NameValueDelimiter:
                            ThrowIfNotExpectedToken(lastCharRead, ' ');
                            state = HeaderParseState.Value;
                            headerBytesLength = 0;
                            break;
                        case HeaderParseState.Value:
                            if (lastCharRead == '\r') // spec mandates \r always precedes \n
                            {
                                string value = HeaderEncoding.GetString(this.receivingBuffer, index: 0, count: headerBytesLength - 1);
                                headers[headerName] = value;
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
                } while (state != HeaderParseState.Terminate);

                int contentLength;
                string contentLengthAsText = headers[ContentLengthHeaderNameText];
                if (!int.TryParse(contentLengthAsText, out contentLength))
                {
                    throw new BadRpcHeaderException(string.Format(CultureInfo.CurrentCulture, Resources.HeaderContentLengthNotParseable, contentLengthAsText));
                }

                Encoding contentEncoding = this.Encoding;
                string contentTypeAsText;
                if (headers.TryGetValue(ContentTypeHeaderNameText, out contentTypeAsText))
                {
                    try
                    {
                        var mediaType = MediaTypeHeaderValue.Parse(contentTypeAsText);
                        if (mediaType.CharSet != null)
                        {
                            contentEncoding = Encoding.GetEncoding(mediaType.CharSet);
                            if (contentEncoding == null)
                            {
                                throw new BadRpcHeaderException($"Unrecognized charset value: '{mediaType.CharSet}'");
                            }
                        }
                    }
                    catch (FormatException ex)
                    {
                        throw new BadRpcHeaderException(ex.Message, ex);
                    }
                }

                byte[] contentBuffer = contentLength <= this.receivingBuffer.Length
                    ? this.receivingBuffer
                    : new byte[contentLength];

                int bytesRead = 0;
                while (bytesRead < contentLength)
                {
                    int bytesJustRead = await this.receivingStream.ReadAsync(contentBuffer, bytesRead, contentLength - bytesRead, cancellationToken).ConfigureAwait(false);
                    if (bytesJustRead == 0)
                    {
                        // Early termination of stream.
                        return null;
                    }

                    bytesRead += bytesJustRead;
                }

                return contentEncoding.GetString(contentBuffer, 0, contentLength);
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

        internal async Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            Verify.Operation(this.sendingStream != null, "No sending stream.");

            using (await this.sendingSemaphore.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                var sendingBufferStream = new MemoryStream(MaxHeaderElementSize);

                // Understand the content we need to send in terms of bytes and length.
                byte[] contentBytes = this.Encoding.GetBytes(json);
                string contentBytesLength = contentBytes.Length.ToString(CultureInfo.InvariantCulture);

                // Transmit the Content-Length header.
                sendingBufferStream.Write(ContentLengthHeaderName, 0, ContentLengthHeaderName.Length);
                sendingBufferStream.Write(HeaderKeyValueDelimiter, 0, HeaderKeyValueDelimiter.Length);
                int headerValueBytesLength = HeaderEncoding.GetBytes(contentBytesLength, 0, contentBytesLength.Length, this.sendingHeaderBuffer, 0);
                sendingBufferStream.Write(this.sendingHeaderBuffer, 0, headerValueBytesLength);
                sendingBufferStream.Write(CrlfBytes, 0, CrlfBytes.Length);

                // Transmit the Content-Type header.
                sendingBufferStream.Write(ContentTypeHeaderName, 0, ContentTypeHeaderName.Length);
                sendingBufferStream.Write(HeaderKeyValueDelimiter, 0, HeaderKeyValueDelimiter.Length);
                var contentTypeHeaderValue = $"application/{this.SubType}; charset={this.Encoding.WebName}";
                headerValueBytesLength = HeaderEncoding.GetBytes(contentTypeHeaderValue, 0, contentTypeHeaderValue.Length, this.sendingHeaderBuffer, 0);
                sendingBufferStream.Write(this.sendingHeaderBuffer, 0, headerValueBytesLength);
                sendingBufferStream.Write(CrlfBytes, 0, CrlfBytes.Length);

                // Terminate the headers.
                sendingBufferStream.Write(CrlfBytes, 0, CrlfBytes.Length);

                // Transmit the headers.
                sendingBufferStream.Position = 0;
                await sendingBufferStream.CopyToAsync(this.sendingStream, MaxHeaderElementSize, cancellationToken).ConfigureAwait(false);

                // Transmit the content itself.
                await this.sendingStream.WriteAsync(contentBytes, 0, contentBytes.Length, cancellationToken).ConfigureAwait(false);
                await this.sendingStream.FlushAsync().ConfigureAwait(false);
            }
        }
    }
}
