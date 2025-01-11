// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Nerdbank.MessagePack;

namespace StreamJsonRpc.Protocol;

[MessagePackConverter(typeof(NerdbankMessagePackFormatter.TraceParentConverter))]
internal unsafe struct TraceParent
{
    internal const int VersionByteCount = 1;
    internal const int ParentIdByteCount = 8;
    internal const int TraceIdByteCount = 16;
    internal const int FlagsByteCount = 1;

    internal byte Version;

    internal fixed byte TraceId[TraceIdByteCount];

    internal fixed byte ParentId[ParentIdByteCount];

    internal TraceFlags Flags;

    internal TraceParent(string? traceparent)
    {
        if (traceparent is null)
        {
            this.Version = 0;
            this.Flags = TraceFlags.None;
            return;
        }

        ReadOnlySpan<char> traceparentChars = traceparent.AsSpan();

        // Decode version
        ReadOnlySpan<char> slice = Consume(ref traceparentChars, VersionByteCount * 2);
        fixed (byte* pVersion = &this.Version)
        {
            Hex.Decode(slice, new Span<byte>(pVersion, 1));
        }

        ConsumeHyphen(ref traceparentChars);

        // Decode traceid
        slice = Consume(ref traceparentChars, TraceIdByteCount * 2);
        fixed (byte* pTraceId = this.TraceId)
        {
            Hex.Decode(slice, new Span<byte>(pTraceId, TraceIdByteCount));
        }

        ConsumeHyphen(ref traceparentChars);

        // Decode parentid
        slice = Consume(ref traceparentChars, ParentIdByteCount * 2);
        fixed (byte* pParentId = this.ParentId)
        {
            Hex.Decode(slice, new Span<byte>(pParentId, ParentIdByteCount));
        }

        ConsumeHyphen(ref traceparentChars);

        // Decode flags
        slice = Consume(ref traceparentChars, FlagsByteCount * 2);
        fixed (TraceFlags* pFlags = &this.Flags)
        {
            Hex.Decode(slice, new Span<byte>(pFlags, 1));
        }

        static void ConsumeHyphen(ref ReadOnlySpan<char> value)
        {
            if (value[0] != '-')
            {
                Requires.Fail("Invalid format.");
            }

            value = value.Slice(1);
        }

        ReadOnlySpan<char> Consume(ref ReadOnlySpan<char> buffer, int length)
        {
            ReadOnlySpan<char> result = buffer.Slice(0, length);
            buffer = buffer.Slice(length);
            return result;
        }
    }

    [Flags]
    internal enum TraceFlags : byte
    {
        /// <summary>
        /// No flags.
        /// </summary>
        None = 0x0,

        /// <summary>
        /// The parent is tracing their action.
        /// </summary>
        Sampled = 0x1,
    }

    internal Guid TraceIdGuid
    {
        get
        {
            fixed (byte* pTraceId = this.TraceId)
            {
                return CopyBufferToGuid(new ReadOnlySpan<byte>(pTraceId, TraceIdByteCount));
            }
        }
    }

    public override string ToString()
    {
        // When calculating the number of characters required, double each 'byte' we have to encode since we're using hex.
        Span<char> traceparent = stackalloc char[(VersionByteCount * 2) + 1 + (TraceIdByteCount * 2) + 1 + (ParentIdByteCount * 2) + 1 + (FlagsByteCount * 2)];
        Span<char> traceParentRemaining = traceparent;

        fixed (byte* pVersion = &this.Version)
        {
            Hex.Encode(new ReadOnlySpan<byte>(pVersion, 1), ref traceParentRemaining);
        }

        AddHyphen(ref traceParentRemaining);

        fixed (byte* pTraceId = this.TraceId)
        {
            Hex.Encode(new ReadOnlySpan<byte>(pTraceId, TraceIdByteCount), ref traceParentRemaining);
        }

        AddHyphen(ref traceParentRemaining);

        fixed (byte* pParentId = this.ParentId)
        {
            Hex.Encode(new ReadOnlySpan<byte>(pParentId, ParentIdByteCount), ref traceParentRemaining);
        }

        AddHyphen(ref traceParentRemaining);

        fixed (TraceFlags* pFlags = &this.Flags)
        {
            Hex.Encode(new ReadOnlySpan<byte>(pFlags, 1), ref traceParentRemaining);
        }

        Debug.Assert(traceParentRemaining.Length == 0, "Characters were not initialized.");

        fixed (char* pValue = traceparent)
        {
            return new string(pValue, 0, traceparent.Length);
        }

        static void AddHyphen(ref Span<char> value)
        {
            value[0] = '-';
            value = value.Slice(1);
        }
    }

    private static unsafe Guid CopyBufferToGuid(ReadOnlySpan<byte> buffer)
    {
        Debug.Assert(buffer.Length == 16, "Guid buffer length mismatch.");
        fixed (byte* pBuffer = buffer)
        {
            return *(Guid*)pBuffer;
        }
    }
}
