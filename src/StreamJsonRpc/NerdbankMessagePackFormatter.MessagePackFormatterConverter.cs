// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
        private readonly FormatterProfile formatterContext;

        internal MessagePackFormatterConverter(FormatterProfile formatterContext)
        {
            this.formatterContext = formatterContext;
        }

#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
        {
            return this.formatterContext.DeserializeObject((RawMessagePack)value, type);
        }

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => this.formatterContext.Deserialize<object>((RawMessagePack)value),
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => this.formatterContext.Deserialize<bool>((RawMessagePack)value);

        public byte ToByte(object value) => this.formatterContext.Deserialize<byte>((RawMessagePack)value);

        public char ToChar(object value) => this.formatterContext.Deserialize<char>((RawMessagePack)value);

        public DateTime ToDateTime(object value) => this.formatterContext.Deserialize<DateTime>((RawMessagePack)value);

        public decimal ToDecimal(object value) => this.formatterContext.Deserialize<decimal>((RawMessagePack)value);

        public double ToDouble(object value) => this.formatterContext.Deserialize<double>((RawMessagePack)value);

        public short ToInt16(object value) => this.formatterContext.Deserialize<short>((RawMessagePack)value);

        public int ToInt32(object value) => this.formatterContext.Deserialize<int>((RawMessagePack)value);

        public long ToInt64(object value) => this.formatterContext.Deserialize<long>((RawMessagePack)value);

        public sbyte ToSByte(object value) => this.formatterContext.Deserialize<sbyte>((RawMessagePack)value);

        public float ToSingle(object value) => this.formatterContext.Deserialize<float>((RawMessagePack)value);

        public string? ToString(object value) => value is null ? null : this.formatterContext.Deserialize<string?>((RawMessagePack)value);

        public ushort ToUInt16(object value) => this.formatterContext.Deserialize<ushort>((RawMessagePack)value);

        public uint ToUInt32(object value) => this.formatterContext.Deserialize<uint>((RawMessagePack)value);

        public ulong ToUInt64(object value) => this.formatterContext.Deserialize<ulong>((RawMessagePack)value);
    }
}
