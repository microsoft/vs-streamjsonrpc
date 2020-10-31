// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Runtime.Serialization;
    using Microsoft;

#pragma warning disable SA1649 // File name should match first type name

    /// <summary>
    /// Flags for the <see cref="TraceParentVersion0Format.TraceFlags"/> field.
    /// </summary>
    [Flags]
    internal enum TraceFlags : byte
    {
        /// <summary>
        /// No flags.
        /// </summary>
        None = 0x00,

        /// <summary>
        /// When set, the least significant bit (right-most), denotes that the caller may have recorded trace data. When unset, the caller did not record trace data out-of-band.
        /// </summary>
        Sampled = 0x01,
    }

    /// <summary>
    /// Contains the fields of the <c>traceparent</c> value as defined by <see href="https://www.w3.org/TR/trace-context/#traceparent-header-field-values">W3C trace-context</see>.
    /// </summary>
    [DataContract]
    internal struct TraceParent
    {
        /// <summary>
        /// The version of the <c>traceparent</c> structure.
        /// </summary>
        [DataMember]
        internal byte Version;

        /// <summary>
        /// Contains the fields for <c>version-format</c> when <see cref="Version"/> is 0.
        /// </summary>
        [DataMember]
        internal TraceParentVersion0Format VersionFormat;

        private const int VersionLength = 1;
        private const int TraceFlagsLength = 1;

        internal unsafe TraceParent(string? httpHeaderEncoding)
        {
            if (string.IsNullOrEmpty(httpHeaderEncoding))
            {
                this.Version = default;
                this.VersionFormat = default;
            }

            ReadOnlySpan<char> headerValue = httpHeaderEncoding.AsSpan();
            fixed (byte* pVersion = &this.Version)
            {
                TraceContextUtilities.HexDecode(headerValue.Slice(0, VersionLength * 2), new Span<byte>(pVersion, VersionLength));
                headerValue = headerValue.Slice(VersionLength * 2);
            }

            ConsumeHyphen(ref headerValue);

            fixed (byte* pTraceId = this.VersionFormat.TraceId)
            {
                TraceContextUtilities.HexDecode(headerValue.Slice(0, TraceParentVersion0Format.TraceIdLength * 2), new Span<byte>(pTraceId, TraceParentVersion0Format.TraceIdLength));
                headerValue = headerValue.Slice(TraceParentVersion0Format.TraceIdLength * 2);
            }

            ConsumeHyphen(ref headerValue);

            fixed (byte* pTraceParentId = this.VersionFormat.ParentId)
            {
                TraceContextUtilities.HexDecode(headerValue.Slice(0, TraceParentVersion0Format.ParentIdLength * 2), new Span<byte>(pTraceParentId, TraceParentVersion0Format.ParentIdLength));
                headerValue = headerValue.Slice(TraceParentVersion0Format.ParentIdLength * 2);
            }

            ConsumeHyphen(ref headerValue);

            fixed (TraceFlags* pFlags = &this.VersionFormat.TraceFlags)
            {
                TraceContextUtilities.HexDecode(headerValue.Slice(0, TraceFlagsLength * 2), new Span<byte>(pFlags, TraceFlagsLength));
                headerValue = headerValue.Slice(TraceFlagsLength * 2);
            }

            static void ConsumeHyphen(ref ReadOnlySpan<char> value)
            {
                if (value[0] != '-')
                {
                    Requires.Fail("Invalid format.");
                }

                value = value.Slice(1);
            }
        }

        public unsafe override string? ToString()
        {
            if (!this.VersionFormat.IsValidParentId())
            {
                return null;
            }

            Span<char> value = stackalloc char[(VersionLength * 2) + 1 + (TraceParentVersion0Format.TraceIdLength * 2) + 1 + (TraceParentVersion0Format.ParentIdLength * 2) + 1 + (TraceFlagsLength * 2)];
            Span<char> remainingValue = value;
            fixed (byte* pVersion = &this.Version)
            {
                TraceContextUtilities.HexEncode(new Span<byte>(pVersion, VersionLength), ref remainingValue);
            }

            AddHyphen(ref remainingValue);

            fixed (byte* pTraceId = this.VersionFormat.TraceId)
            {
                TraceContextUtilities.HexEncode(new Span<byte>(pTraceId, TraceParentVersion0Format.TraceIdLength), ref remainingValue);
            }

            AddHyphen(ref remainingValue);

            fixed (byte* pTraceParentId = this.VersionFormat.ParentId)
            {
                TraceContextUtilities.HexEncode(new Span<byte>(pTraceParentId, TraceParentVersion0Format.ParentIdLength), ref remainingValue);
            }

            AddHyphen(ref remainingValue);

            fixed (TraceFlags* pFlags = &this.VersionFormat.TraceFlags)
            {
                TraceContextUtilities.HexEncode(new Span<byte>(pFlags, TraceFlagsLength), ref remainingValue);
            }

            fixed (char* pValue = value)
            {
                return new string(pValue, 0, value.Length);
            }

            static void AddHyphen(ref Span<char> value)
            {
                value[0] = '-';
                value = value.Slice(1);
            }
        }
    }

    /// <summary>
    /// Contains the fields of the <c>traceparent</c>'s <c>version-format</c> value as defined by <see href="https://www.w3.org/TR/trace-context/#version-format">W3C trace-context</see>.
    /// </summary>
    [DataContract]
    internal unsafe struct TraceParentVersion0Format
    {
        internal const int TraceIdLength = 16;
        internal const int ParentIdLength = 8;

        /// <summary>
        /// The ID of the whole trace forest and is used to uniquely identify a distributed trace through a system.
        /// It is represented as a 16-byte array, for example, 4bf92f3577b34da6a3ce929d0e0e4736.
        /// All bytes as zero (00000000000000000000000000000000) is considered an invalid value.
        /// </summary>
        [DataMember]
        internal fixed byte TraceId[TraceIdLength];

        /// <summary>
        /// The ID of this request as known by the caller (in some tracing systems, this is known as the span-id, where a span is the execution of a client request).
        /// It is represented as an 8-byte array, for example, 00f067aa0ba902b7.
        /// All bytes as zero (0000000000000000) is considered an invalid value.
        /// </summary>
        [DataMember]
        internal fixed byte ParentId[ParentIdLength];

        /// <summary>
        /// An 8-bit field that controls tracing flags such as sampling, trace level, etc.
        /// These flags are recommendations given by the caller rather than strict rules to follow for three reasons:
        /// <list type="number">
        /// <item>Trust and abuse</item>
        /// <item>Bug in the caller</item>
        /// <item>Different load between caller service and callee service might force callee to downsample</item>
        /// </list>
        /// </summary>
        [DataMember]
        internal TraceFlags TraceFlags;

        internal TraceParentVersion0Format(Guid traceId, ReadOnlySpan<byte> parentId)
        {
            if (traceId != Guid.Empty && IsValidParentId(parentId))
            {
                var traceIdSrcSpan = new ReadOnlySpan<byte>(&traceId, sizeof(Guid));
                fixed (byte* pTraceId = this.TraceId)
                {
                    var traceIdDestSpan = new Span<byte>(pTraceId, 16);
                    traceIdSrcSpan.CopyTo(traceIdDestSpan);
                }

                fixed (byte* pParentId = this.ParentId)
                {
                    parentId.CopyTo(new Span<byte>(pParentId, 8));
                }
            }

            this.TraceFlags = TraceFlags.None;
        }

        internal Guid TraceIdGuid
        {
            get
            {
                fixed (byte* pTraceId = this.TraceId)
                {
                    return MemoryMarshal.Cast<byte, Guid>(new ReadOnlySpan<byte>(pTraceId, TraceIdLength))[0];
                }
            }

            set
            {
                fixed (byte* pTraceId = this.TraceId)
                {
                    MemoryMarshal.Cast<byte, Guid>(new Span<byte>(pTraceId, TraceIdLength))[0] = value;
                }
            }
        }

        /// <summary>
        /// Tests a given <c>parent-id</c> value to see if it is allowed.
        /// </summary>
        /// <param name="parentId">The value to test.</param>
        /// <returns><see langword="true"/> if the <paramref name="parentId"/> is valid; otherwise <see langword="false"/>.</returns>
        internal static bool IsValidParentId(ReadOnlySpan<byte> parentId)
        {
            if (parentId.Length != 8)
            {
                return false;
            }

            bool nonZeroObserved = false;
            for (int i = 0; i < parentId.Length; i++)
            {
                if (parentId[i] != 0)
                {
                    nonZeroObserved = true;
                    break;
                }
            }

            return nonZeroObserved;
        }

        internal bool IsValidParentId()
        {
            fixed (byte* pParentId = this.ParentId)
            {
                var parentId = new ReadOnlySpan<byte>(pParentId, 8);
                return IsValidParentId(parentId);
            }
        }
    }

    /// <summary>
    /// Contains the fields of the <c>tracestate</c> value as defined by <see href="https://www.w3.org/TR/trace-context/#tracestate-header-field-values">W3C trace-context</see>.
    /// </summary>
    [DataContract]
    internal struct TraceState
    {
        /// <summary>
        /// Gets the list of key=value pairs that stores the data in <c>tracestate</c>.
        /// </summary>
        [DataMember]
        internal Memory<KeyValuePair<string, string>> Values;

        internal TraceState(string? value)
        {
            // TODO: implement this.
            this.Values = default;
        }

        public override string? ToString()
        {
            // TODO: implement this.
            return null;
        }
    }

    internal static class TraceContextUtilities
    {
        private static readonly byte[] HexBytes = new byte[] { (byte)'0', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9', (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f' };
        private static readonly byte[] ReverseHexDigits = BuildReverseHexDigits();

        internal static void HexEncode(ReadOnlySpan<byte> src, ref Span<char> dest)
        {
            Span<byte> bytes = MemoryMarshal.Cast<char, byte>(dest);

            // Inspired by http://stackoverflow.com/questions/623104/c-byte-to-hex-string/3974535#3974535
            int lengthInNibbles = src.Length * 2;

            for (int i = 0; i < (lengthInNibbles & -2); i++)
            {
                int index0 = +i >> 1;
                var b = (byte)(src[index0] >> 4);
                bytes[(2 * i) + 1] = 0;
                bytes[2 * i++] = HexBytes[b];

                b = (byte)(src[index0] & 0x0F);
                bytes[(2 * i) + 1] = 0;
                bytes[2 * i] = HexBytes[b];
            }

            dest = dest.Slice(lengthInNibbles);
        }

        internal static void HexDecode(ReadOnlySpan<char> value, Span<byte> bytes)
        {
            for (int i = 0; i < value.Length; i++)
            {
                int c1 = ReverseHexDigits[value[i++] - '0'] << 4;
                int c2 = ReverseHexDigits[value[i] - '0'];

                bytes[i >> 1] = (byte)(c1 + c2);
            }
        }

        private static byte[] BuildReverseHexDigits()
        {
            var bytes = new byte['f' - '0' + 1];

            for (int i = 0; i < 10; i++)
            {
                bytes[i] = (byte)i;
            }

            for (int i = 10; i < 16; i++)
            {
                bytes[i + 'a' - '0' - 0x0a] = (byte)i;
            }

            return bytes;
        }
    }
}
