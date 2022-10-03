﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// An abstract base class for for sending and receiving messages
/// using <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
/// </summary>
public abstract class PipeMessageHandler : MessageHandlerBase, IJsonRpcMessageBufferManager
{
    /// <summary>
    /// The largest size of a message to buffer completely before deserialization begins
    /// when we have an async deserializing alternative from the formatter.
    /// </summary>
    /// <remarks>
    /// This value is chosen to match the default buffer size for the <see cref="PipeOptions"/> class
    /// since exceeding the <see cref="PipeOptions.PauseWriterThreshold"/> would cause an exception
    /// when we call <see cref="PipeReader.AdvanceTo(SequencePosition, SequencePosition)"/> to wait for more data.
    /// </remarks>
    private static readonly long LargeMessageThreshold = new PipeOptions().PauseWriterThreshold;

    private (IJsonRpcMessageBufferManager Message, SequencePosition ConsumedPosition) deserializationReservedBuffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
    /// </summary>
    /// <param name="pipe">The reader and writer to use for receiving/transmitting messages.</param>
    /// <param name="formatter">The formatter used to serialize messages.</param>
    public PipeMessageHandler(IDuplexPipe pipe, IJsonRpcMessageFormatter formatter)
        : this(Requires.NotNull(pipe, nameof(pipe)).Output, Requires.NotNull(pipe, nameof(pipe)).Input, formatter)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
    /// </summary>
    /// <param name="writer">The writer to use for transmitting messages.</param>
    /// <param name="reader">The reader to use for receiving messages.</param>
    /// <param name="formatter">The formatter used to serialize messages.</param>
    public PipeMessageHandler(PipeWriter? writer, PipeReader? reader, IJsonRpcMessageFormatter formatter)
        : base(formatter)
    {
        this.Reader = reader;
        this.Writer = writer;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PipeMessageHandler"/> class.
    /// </summary>
    /// <param name="writer">The stream to use for transmitting messages.</param>
    /// <param name="reader">The stream to use for receiving messages.</param>
    /// <param name="formatter">The formatter used to serialize messages.</param>
    public PipeMessageHandler(Stream? writer, Stream? reader, IJsonRpcMessageFormatter formatter)
        : base(formatter)
    {
        this.Reader = reader is object ? PipeReader.Create(reader, new StreamPipeReaderOptions(leaveOpen: true)) : null;
        this.Writer = writer?.UsePipeWriter();

        // After we've completed writing, only dispose the underlying write stream when we've flushed everything.
        if (writer is not null)
        {
            Assumes.NotNull(this.Writer);
#pragma warning disable CS0618 // Type or member is obsolete (Nerdbank.Streams implements this, so it won't go away).
            this.Writer.OnReaderCompleted((ex, state) => ((Stream)state!).Dispose(), writer);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        // NamedPipeClientStream.ReadAsync(byte[], int, int, CancellationToken) ignores the CancellationToken except at the entrypoint.
        // To avoid an async hang there or in similar streams upon disposal, we're going to Dispose the read stream directly.
        // We only need to do this if the read stream is distinct from the write stream, which is already handled above.
        if (reader is not null && reader != writer)
        {
            this.DisposalToken.Register(state => ((Stream)state!).Dispose(), reader);
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => this.Reader is not null;

    /// <inheritdoc/>
    public override bool CanWrite => this.Writer is not null;

    /// <summary>
    /// Gets the reader to use for receiving messages.
    /// </summary>
    protected PipeReader? Reader { get; }

    /// <summary>
    /// Gets the writer to use for transmitting messages.
    /// </summary>
    protected PipeWriter? Writer { get; }

    /// <inheritdoc/>
    void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
    {
        if (message is not null && this.Reader is not null && this.deserializationReservedBuffer.Message == message)
        {
            this.deserializationReservedBuffer.Message.DeserializationComplete(message);
            this.Reader.AdvanceTo(this.deserializationReservedBuffer.ConsumedPosition);
            this.deserializationReservedBuffer = default;
        }
    }

    /// <inheritdoc/>
    protected sealed override ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
    {
#pragma warning disable VSTHRD103 // Call async methods when in an async method
        this.Write(content, cancellationToken);
#pragma warning restore VSTHRD103 // Call async methods when in an async method
        return default;
    }

    /// <summary>
    /// Writes a message to the pipe.
    /// </summary>
    /// <param name="content">The message to write.</param>
    /// <param name="cancellationToken">A token to cancel the transmission.</param>
    /// <remarks>
    /// Implementations may assume the method is never called before the previous call has completed.
    /// They can assume their caller will invoke <see cref="PipeWriter.FlushAsync(CancellationToken)"/> on their behalf
    /// after writing is completed.
    /// </remarks>
    protected abstract void Write(JsonRpcMessage content, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override void DisposeReader()
    {
        this.deserializationReservedBuffer = default;
        this.Reader?.Complete();

        base.DisposeReader();
    }

    /// <inheritdoc />
    protected override void DisposeWriter()
    {
        this.Writer?.Complete();

        base.DisposeWriter();
    }

    /// <inheritdoc />
    protected override async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Verify.Operation(this.Writer is not null, "No sending stream.");
        await this.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads from the <see cref="Reader"/> until at least a specified number of bytes are available.
    /// </summary>
    /// <param name="requiredBytes">The number of bytes that must be available.</param>
    /// <param name="allowEmpty"><see langword="true"/> to allow returning 0 bytes if the end of the stream is encountered before any bytes are read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The <see cref="ReadResult"/> containing at least <paramref name="requiredBytes"/> bytes.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <see cref="ReadResult.IsCanceled"/>.</exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <see cref="ReadResult.IsCompleted"/> before we have <paramref name="requiredBytes"/> bytes.
    /// Not thrown if 0 bytes were read and <paramref name="allowEmpty"/> is <see langword="true"/>.
    /// </exception>
    protected async ValueTask<ReadResult> ReadAtLeastAsync(int requiredBytes, bool allowEmpty, CancellationToken cancellationToken)
    {
        Assumes.NotNull(this.Reader);
        ReadResult readResult = await this.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        while (readResult.Buffer.Length < requiredBytes && !readResult.IsCompleted && !readResult.IsCanceled)
        {
            this.Reader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
            readResult = await this.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (allowEmpty && readResult.Buffer.Length == 0)
        {
            return readResult;
        }

        if (readResult.Buffer.Length < requiredBytes)
        {
            throw readResult.IsCompleted ? new EndOfStreamException() :
                readResult.IsCanceled ? new OperationCanceledException() :
                Assumes.NotReachable();
        }

        return readResult;
    }

    /// <summary>
    /// Deserializes a JSON-RPC message using the <see cref="MessageHandlerBase.Formatter"/>.
    /// </summary>
    /// <param name="contentLength">The length of the JSON-RPC message.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deserialized message.</returns>
    private protected ValueTask<JsonRpcMessage> DeserializeMessageAsync(int contentLength, CancellationToken cancellationToken) => this.DeserializeMessageAsync(contentLength, null, null, cancellationToken);

    /// <summary>
    /// Deserializes a JSON-RPC message using the <see cref="MessageHandlerBase.Formatter"/>.
    /// </summary>
    /// <param name="contentLength">The length of the JSON-RPC message.</param>
    /// <param name="specificEncoding">The encoding to use during deserialization, as specified in a header for this particular message.</param>
    /// <param name="defaultEncoding">The encoding to use when <paramref name="specificEncoding"/> is <see langword="null"/> if the <see cref="MessageHandlerBase.Formatter"/> supports encoding.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deserialized message.</returns>
    /// <exception cref="NotSupportedException">Thrown if <paramref name="specificEncoding"/> is non-null and the formatter does not implement the appropriate interface to supply the encoding.</exception>
    private protected async ValueTask<JsonRpcMessage> DeserializeMessageAsync(int contentLength, Encoding? specificEncoding, Encoding? defaultEncoding, CancellationToken cancellationToken)
    {
        Requires.Range(contentLength > 0, nameof(contentLength));
        Assumes.NotNull(this.Reader);
        Assumes.Null(this.deserializationReservedBuffer.Message); // Previous message holds buffers must have been released by now.
        Encoding? contentEncoding = specificEncoding ?? defaultEncoding;

        // Being async during deserialization increases GC pressure,
        // so prefer getting all bytes into a buffer first if the message is a reasonably small size.
        if (contentLength >= LargeMessageThreshold && this.Formatter is IJsonRpcAsyncMessageFormatter asyncFormatter)
        {
            PipeReader slice = this.Reader.ReadSlice(contentLength);
            if (contentEncoding is not null && asyncFormatter is IJsonRpcAsyncMessageTextFormatter asyncTextFormatter)
            {
                return await asyncTextFormatter.DeserializeAsync(slice, contentEncoding, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (specificEncoding is not null)
                {
                    this.ThrowNoTextEncoder();
                }

                return await asyncFormatter.DeserializeAsync(slice, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            ReadResult readResult = await this.ReadAtLeastAsync(contentLength, allowEmpty: false, cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> contentBuffer = readResult.Buffer.Slice(0, contentLength);
            try
            {
                JsonRpcMessage message;
                if (contentEncoding is not null && this.Formatter is IJsonRpcMessageTextFormatter textFormatter)
                {
                    message = textFormatter.Deserialize(contentBuffer, contentEncoding);
                }
                else
                {
                    if (specificEncoding is not null)
                    {
                        this.ThrowNoTextEncoder();
                    }

                    message = this.Formatter.Deserialize(contentBuffer);
                }

                if (message is IJsonRpcMessageBufferManager bufferedMessage)
                {
                    this.deserializationReservedBuffer = (bufferedMessage, contentBuffer.End);
                }

                return message;
            }
            finally
            {
                if (this.deserializationReservedBuffer.Message is null)
                {
                    // We're now done reading from the pipe's buffer. We can release it now.
                    this.Reader.AdvanceTo(contentBuffer.End);
                }
            }
        }
    }

    private protected Exception ThrowNoTextEncoder()
    {
        throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.TextEncoderNotApplicable, this.Formatter.GetType().FullName, typeof(IJsonRpcMessageTextFormatter).FullName));
    }
}
