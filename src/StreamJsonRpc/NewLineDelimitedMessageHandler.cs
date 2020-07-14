// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipelines;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// A JSON-RPC message handler that delimits messages with new lines.
    /// </summary>
    /// <remarks>
    /// When reading messages, either \n or \r\n character sequences are permitted for new lines.
    /// When writing messages the <see cref="NewLine"/> property controls which character sequence is used to terminate each message.
    /// </remarks>
    public class NewLineDelimitedMessageHandler : PipeMessageHandler
    {
        /// <summary>
        /// Backing field for the <see cref="NewLine"/> property.
        /// </summary>
        private NewLineStyle newLine = NewLineStyle.CrLf;

        /// <summary>
        /// The bytes to write out as the new line after each message.
        /// </summary>
        private ReadOnlyMemory<byte> newLineBytes;

        /// <summary>
        /// Initializes a new instance of the <see cref="NewLineDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="pipe">The reader and writer to use for receiving/transmitting messages.</param>
        /// <param name="formatter">The formatter used to serialize messages. Only UTF-8 formatters are supported.</param>
        public NewLineDelimitedMessageHandler(IDuplexPipe pipe, IJsonRpcMessageTextFormatter formatter)
            : base(pipe, formatter)
        {
            this.CommonConstructor();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NewLineDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The writer to use for transmitting messages.</param>
        /// <param name="reader">The reader to use for receiving messages.</param>
        /// <param name="formatter">The formatter used to serialize messages. Only UTF-8 formatters are supported.</param>
        public NewLineDelimitedMessageHandler(PipeWriter? writer, PipeReader? reader, IJsonRpcMessageTextFormatter formatter)
            : base(writer, reader, formatter)
        {
            this.CommonConstructor();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NewLineDelimitedMessageHandler"/> class.
        /// </summary>
        /// <param name="writer">The stream to use for transmitting messages.</param>
        /// <param name="reader">The stream to use for receiving messages.</param>
        /// <param name="formatter">The formatter used to serialize messages. Only UTF-8 formatters are supported.</param>
        public NewLineDelimitedMessageHandler(Stream writer, Stream reader, IJsonRpcMessageTextFormatter formatter)
            : base(writer, reader, formatter)
        {
            this.CommonConstructor();
        }

        /// <summary>
        /// Describes the supported styles of new lines that can be written.
        /// </summary>
        public enum NewLineStyle
        {
            /// <summary>
            /// Newlines are represented as a single \n character.
            /// </summary>
            Lf,

            /// <summary>
            /// Newlines are represented by a \r\n character sequence.
            /// </summary>
            CrLf,
        }

        /// <summary>
        /// Gets or sets the new line sequence to use to terminate a JSON-RPC message.
        /// </summary>
        public NewLineStyle NewLine
        {
            get => this.newLine;
            set
            {
                if (this.newLine != value)
                {
                    this.newLineBytes = GetLineFeedSequence(this.Formatter.Encoding, value);
                    this.newLine = value;
                }
            }
        }

        /// <inheritdoc cref="MessageHandlerBase.Formatter"/>
        public new IJsonRpcMessageTextFormatter Formatter => (IJsonRpcMessageTextFormatter)base.Formatter;

        /// <inheritdoc/>
        protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Assumes.NotNull(this.Writer);

            cancellationToken.ThrowIfCancellationRequested();
            this.Formatter.Serialize(this.Writer, content);
            this.Writer.Write(this.newLineBytes.Span);
        }

        /// <inheritdoc/>
        protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
        {
            Assumes.NotNull(this.Reader);
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

                // If the line ends with an \r (that precedes the \n we already found), trim that as well.
                SequencePosition? cr = line.PositionOf((byte)'\r');
                if (cr.HasValue && line.GetPosition(1, cr.Value).Equals(lf))
                {
                    line = line.Slice(0, line.Length - 1);
                }

                try
                {
                    // Skip over blank lines.
                    if (line.Length > 0)
                    {
                        return this.Formatter.Deserialize(line);
                    }
                }
                finally
                {
                    // Advance to the next line.
                    this.Reader.AdvanceTo(readResult.Buffer.GetPosition(1, lf.Value));
                }
            }
        }

        /// <summary>
        /// Gets the byte sequence for new lines.
        /// </summary>
        /// <param name="encoding">The encoding to use to convert the new line characters to bytes.</param>
        /// <param name="style">The style of new line to produce.</param>
        /// <returns>The bytes to emit for each new line.</returns>
        private static ReadOnlyMemory<byte> GetLineFeedSequence(Encoding encoding, NewLineStyle style)
        {
            return style switch
            {
                NewLineStyle.Lf => encoding.GetBytes("\n"),
                NewLineStyle.CrLf => encoding.GetBytes("\r\n"),
                _ => throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Resources.EnumValueNotRecognized, style), nameof(style)),
            };
        }

        /// <summary>
        /// Validates and initializes fields as they should be from every constructor.
        /// </summary>
        private void CommonConstructor()
        {
            if (this.Formatter is IJsonRpcFormatterTracingCallbacks)
            {
                // Such a formatter requires that we make their own written bytes available back to them for logging purposes.
                // We haven't implemented support for such, and the JsonMessageFormatter doesn't need it, so no need to.
                throw new NotSupportedException("Formatters that implement " + nameof(IJsonRpcFormatterTracingCallbacks) + " are not supported. Try using " + nameof(JsonMessageFormatter) + ".");
            }

            if (this.Formatter.Encoding.WebName != Encoding.UTF8.WebName)
            {
                throw new NotSupportedException("Only UTF-8 formatters are supported.");
            }

            this.newLineBytes = GetLineFeedSequence(this.Formatter.Encoding, this.NewLine);
        }
    }
}
