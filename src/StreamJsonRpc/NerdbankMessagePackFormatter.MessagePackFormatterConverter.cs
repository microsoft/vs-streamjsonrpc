﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.Serialization;
using Nerdbank.MessagePack;
using PolyType;
using PolyType.ReflectionProvider;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private partial class MessagePackFormatterConverter(MessagePackSerializer serializer) : IFormatterConverter
    {
#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
        {
            MessagePackReader reader = this.CreateReader(value);
            return serializer.DeserializeObject(ref reader, ReflectionTypeShapeProvider.Default.GetShape(type));
        }

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => new object(),
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => this.CreateReader(value).ReadBoolean();

        public byte ToByte(object value) => this.CreateReader(value).ReadByte();

        public char ToChar(object value) => this.CreateReader(value).ReadChar();

        public DateTime ToDateTime(object value) => this.CreateReader(value).ReadDateTime();

        public decimal ToDecimal(object value) => serializer.Deserialize<decimal>((RawMessagePack)value, Witness.ShapeProvider);

        public double ToDouble(object value) => this.CreateReader(value).ReadDouble();

        public short ToInt16(object value) => this.CreateReader(value).ReadInt16();

        public int ToInt32(object value) => this.CreateReader(value).ReadInt32();

        public long ToInt64(object value) => this.CreateReader(value).ReadInt64();

        public sbyte ToSByte(object value) => this.CreateReader(value).ReadSByte();

        public float ToSingle(object value) => this.CreateReader(value).ReadSingle();

        public string? ToString(object value) => value is null ? null : this.CreateReader(value).ReadString();

        public ushort ToUInt16(object value) => this.CreateReader(value).ReadUInt16();

        public uint ToUInt32(object value) => this.CreateReader(value).ReadUInt32();

        public ulong ToUInt64(object value) => this.CreateReader(value).ReadUInt64();

        private MessagePackReader CreateReader(object value) => new((RawMessagePack)value);

        [GenerateShape<decimal>]
        private partial class Witness;
    }
}
