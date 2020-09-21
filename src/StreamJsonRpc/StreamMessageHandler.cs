// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An abstract base class for for sending and receiving messages over a
    /// reading and writing pair of <see cref="Stream"/> objects.
    /// </summary>
    public abstract class StreamMessageHandler : MessageHandlerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamMessageHandler"/> class.
        /// </summary>
        /// <param name="sendingStream">The stream used to transmit messages. May be null.</param>
        /// <param name="receivingStream">The stream used to receive messages. May be null.</param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        protected StreamMessageHandler(Stream? sendingStream, Stream? receivingStream, IJsonRpcMessageFormatter formatter)
            : base(formatter)
        {
            Requires.Argument(sendingStream == null || sendingStream.CanWrite, nameof(sendingStream), Resources.StreamMustBeWriteable);
            Requires.Argument(receivingStream == null || receivingStream.CanRead, nameof(receivingStream), Resources.StreamMustBeReadable);

            this.SendingStream = sendingStream;
            this.ReceivingStream = receivingStream;
        }

        /// <summary>
        /// Gets a value indicating whether this message handler has a receiving stream.
        /// </summary>
        public override bool CanRead => this.ReceivingStream != null;

        /// <summary>
        /// Gets a value indicating whether this message handler has a sending stream.
        /// </summary>
        public override bool CanWrite => this.SendingStream != null;

        /// <summary>
        /// Gets the stream used to transmit messages. May be null.
        /// </summary>
        protected Stream? SendingStream { get; }

        /// <summary>
        /// Gets the stream used to receive messages. May be null.
        /// </summary>
        protected Stream? ReceivingStream { get; }

        /// <summary>
        /// Disposes resources allocated by this instance.
        /// </summary>
        /// <param name="disposing"><c>true</c> when being disposed; <c>false</c> when being finalized.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.ReceivingStream?.Dispose();
                this.SendingStream?.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Calls <see cref="Stream.FlushAsync()"/> on the <see cref="SendingStream"/>,
        /// or equivalent sending stream if using an alternate transport.
        /// </summary>
        /// <returns>A <see cref="Task"/> that completes when the write buffer has been transmitted.</returns>
        protected override ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            Verify.Operation(this.SendingStream != null, "No sending stream.");
            return new ValueTask(this.SendingStream.FlushAsync(cancellationToken));
        }
    }
}
