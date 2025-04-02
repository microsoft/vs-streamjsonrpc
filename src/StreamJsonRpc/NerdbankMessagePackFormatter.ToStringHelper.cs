// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// A recyclable object that can serialize a message to JSON on demand.
    /// </summary>
    /// <remarks>
    /// In perf traces, creation of this object used to show up as one of the most allocated objects.
    /// It is used even when tracing isn't active. So we changed its design to be reused,
    /// since its lifetime is only required during a synchronous call to a trace API.
    /// </remarks>
    private class ToStringHelper
    {
        private ReadOnlySequence<byte>? encodedMessage;
        private string? jsonString;

        public override string ToString()
        {
            Verify.Operation(this.encodedMessage.HasValue, "This object has not been activated. It may have already been recycled.");

            return this.jsonString ??= MessagePackSerializer.ConvertToJson(this.encodedMessage.Value);
        }

        /// <summary>
        /// Initializes this object to represent a message.
        /// </summary>
        internal void Activate(ReadOnlySequence<byte> encodedMessage)
        {
            this.encodedMessage = encodedMessage;
        }

        /// <summary>
        /// Cleans out this object to release memory and ensure <see cref="ToString"/> throws if someone uses it after deactivation.
        /// </summary>
        internal void Deactivate()
        {
            this.encodedMessage = null;
            this.jsonString = null;
        }
    }
}
