// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    internal class SerializationHelper
    {
        /// <summary>
        /// A temporary buffer used to serialize a <see cref="JToken"/>. Lazily initialized.
        /// </summary>
        private Sequence<byte> sendingContentSequence;

        /// <summary>
        /// A recycled <see cref="StreamWriter"/> used to write to <see cref="sendingContentSequence"/>. Lazily initialized.
        /// </summary>
        private StreamWriter sendingContentBufferStreamWriter;

        /// <summary>
        /// A recycled <see cref="JsonWriter"/> used to write to <see cref="sendingContentBufferStreamWriter"/>. Lazily initialized.
        /// </summary>
        private JsonWriter sendingContentBufferStreamJsonWriter;

        /// <summary>
        /// Serializes a <see cref="JToken"/> to a memory stream and returns the result.
        /// </summary>
        /// <param name="content">The <see cref="JToken"/> to serialize.</param>
        /// <param name="encoding">The text encoding to use.</param>
        /// <returns>A <see cref="MemoryStream"/> with the serialized content, positioned at 0.</returns>
        /// <remarks>
        /// The returned <see cref="MemoryStream"/> is recycled for each call. This method should *not* be invoked
        /// until any prior invocation's result is no longer necessary.
        /// </remarks>
        internal ReadOnlySequence<byte> Serialize(JToken content, Encoding encoding)
        {
            Requires.NotNull(content, nameof(content));
            Requires.NotNull(encoding, nameof(encoding));

            if (this.sendingContentSequence == null)
            {
                this.sendingContentSequence = new Sequence<byte>();
            }

            if (this.sendingContentBufferStreamWriter == null || this.sendingContentBufferStreamWriter.Encoding != encoding)
            {
                this.sendingContentBufferStreamWriter = new StreamWriter(this.sendingContentSequence.AsStream(), encoding);
                this.sendingContentBufferStreamJsonWriter = null; // we'll need to reinitialize this for the new StreamWriter
            }

            if (this.sendingContentBufferStreamJsonWriter == null)
            {
                this.sendingContentBufferStreamJsonWriter = new JsonTextWriter(this.sendingContentBufferStreamWriter);
            }

            content.WriteTo(this.sendingContentBufferStreamJsonWriter);
            this.sendingContentBufferStreamWriter.Flush(); // this flushes the internal encoder so it's safe to reuse

            return this.sendingContentSequence;
        }

        /// <summary>
        /// Releases memory from the last serialization.
        /// </summary>
        internal void Reset()
        {
            this.sendingContentSequence.Reset();
        }
    }
}
