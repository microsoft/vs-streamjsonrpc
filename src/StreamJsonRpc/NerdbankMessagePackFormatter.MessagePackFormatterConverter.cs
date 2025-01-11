// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.Serialization;
using Nerdbank.MessagePack;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private class MessagePackFormatterConverter : IFormatterConverter
    {
        private readonly Profile formatterContext;

        internal MessagePackFormatterConverter(Profile formatterContext)
        {
            this.formatterContext = formatterContext;
        }

#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
        {
            return this.formatterContext.DeserializeObject((ReadOnlySequence<byte>)value, type);
        }

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => this.formatterContext.Deserialize<object>((ReadOnlySequence<byte>)value)!,
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => this.formatterContext.Deserialize<bool>((ReadOnlySequence<byte>)value);

        public byte ToByte(object value) => this.formatterContext.Deserialize<byte>((ReadOnlySequence<byte>)value);

        public char ToChar(object value) => this.formatterContext.Deserialize<char>((ReadOnlySequence<byte>)value);

        public DateTime ToDateTime(object value) => this.formatterContext.Deserialize<DateTime>((ReadOnlySequence<byte>)value);

        public decimal ToDecimal(object value) => this.formatterContext.Deserialize<decimal>((ReadOnlySequence<byte>)value);

        public double ToDouble(object value) => this.formatterContext.Deserialize<double>((ReadOnlySequence<byte>)value);

        public short ToInt16(object value) => this.formatterContext.Deserialize<short>((ReadOnlySequence<byte>)value);

        public int ToInt32(object value) => this.formatterContext.Deserialize<int>((ReadOnlySequence<byte>)value);

        public long ToInt64(object value) => this.formatterContext.Deserialize<long>((ReadOnlySequence<byte>)value);

        public sbyte ToSByte(object value) => this.formatterContext.Deserialize<sbyte>((ReadOnlySequence<byte>)value);

        public float ToSingle(object value) => this.formatterContext.Deserialize<float>((ReadOnlySequence<byte>)value);

        public string? ToString(object value) => value is null ? null : this.formatterContext.Deserialize<string?>((ReadOnlySequence<byte>)value);

        public ushort ToUInt16(object value) => this.formatterContext.Deserialize<ushort>((ReadOnlySequence<byte>)value);

        public uint ToUInt32(object value) => this.formatterContext.Deserialize<uint>((ReadOnlySequence<byte>)value);

        public ulong ToUInt64(object value) => this.formatterContext.Deserialize<ulong>((ReadOnlySequence<byte>)value);
    }
}
