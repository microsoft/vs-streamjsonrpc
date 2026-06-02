// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;

namespace StreamJsonRpc.Protocol;

internal unsafe struct TraceParent
{
    internal const int VersionByteCount = 1;
    internal const int ParentIdByteCount = 8;
    internal const int TraceIdByteCount = 16;
    internal const int FlagsByteCount = 1;

    /// <summary>
    /// The number of characters in a serialized traceparent value.
    /// </summary>
    /// <devremarks>
    /// When calculating the number of characters required, double each 'byte' we have to encode since we're using hex.
    /// </devremarks>
    internal const int Length = (VersionByteCount * 2) + 1 + (TraceIdByteCount * 2) + 1 + (ParentIdByteCount * 2) + 1 + (FlagsByteCount * 2);

    internal byte Version;

    internal fixed byte TraceId[TraceIdByteCount];

    internal fixed byte ParentId[ParentIdByteCount];

    internal TraceFlags Flags;

    internal TraceParent(string? traceparent)
        : this(traceparent is null ? default : traceparent.AsSpan())
    {
    }

    internal TraceParent(ReadOnlySpan<char> traceparent)
    {
        if (traceparent is [])
        {
            this.Version = 0;
            this.Flags = TraceFlags.None;
            return;
        }

        // Decode version
        ReadOnlySpan<char> slice = Consume(ref traceparent, VersionByteCount * 2);
        fixed (byte* pVersion = &this.Version)
        {
            Hex.Decode(slice, new Span<byte>(pVersion, 1));
        }

        ConsumeHyphen(ref traceparent);

        // Decode traceid
        slice = Consume(ref traceparent, TraceIdByteCount * 2);
        fixed (byte* pTraceId = this.TraceId)
        {
            Hex.Decode(slice, new Span<byte>(pTraceId, TraceIdByteCount));
        }

        ConsumeHyphen(ref traceparent);

        // Decode parentid
        slice = Consume(ref traceparent, ParentIdByteCount * 2);
        fixed (byte* pParentId = this.ParentId)
        {
            Hex.Decode(slice, new Span<byte>(pParentId, ParentIdByteCount));
        }

        ConsumeHyphen(ref traceparent);

        // Decode flags
        slice = Consume(ref traceparent, FlagsByteCount * 2);
        fixed (TraceFlags* pFlags = &this.Flags)
        {
            Hex.Decode(slice, new Span<byte>(pFlags, 1));
        }

        Requires.Argument(traceparent is [], nameof(traceparent), "Expected traceparent to be fully consumed.");

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
        Span<char> chars = stackalloc char[Length];
        this.WriteTo(chars);
        return chars.ToString();
    }

    /// <summary>
    /// Serializes the <see cref="TraceParent"/> value as a string.
    /// </summary>
    /// <param name="destination">The span to write to. This must be at least <see cref="Length"/> in length.</param>
    /// <returns>The number of characters written to <paramref name="destination"/>. Always equal to <see cref="Length"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="destination"/> is shorter than <see cref="Length"/>.</exception>
    internal int WriteTo(Span<char> destination)
    {
        Requires.Argument(destination.Length >= Length, nameof(destination), $"Destination must be at least {Length} characters in length.");

        Span<char> traceParentRemaining = destination;

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

        Debug.Assert(traceParentRemaining is [], "Characters were not initialized.");

        return Length;

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
