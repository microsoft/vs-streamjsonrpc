// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.Serialization;
using Nerdbank.MessagePack;
using PolyType;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private partial class MessagePackFormatterConverter(NerdbankMessagePackFormatter formatter) : IFormatterConverter
    {
#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
        {
            // We don't support serializing/deserializing the non-generic IDictionary,
            // since it uses untyped keys and values which we cannot securely hash.
            if (type == typeof(System.Collections.IDictionary))
            {
                // Force us to deserialize into a semi-typed dictionary.
                // The string key is a reasonable 99% compatible assumption, and allows us to securely hash the keys.
                // The untyped values will be alright because we support the primitives types.
                type = typeof(Dictionary<string, object?>);
            }

            MessagePackReader reader = this.CreateReader(value);
            try
            {
                return formatter.UserDataSerializer.DeserializeObject(ref reader, formatter.GetUserDataShape(type));
            }
            catch (Exception ex)
            {
                formatter.JsonRpc?.TraceSource.TraceData(TraceEventType.Error, (int)JsonRpc.TraceEvents.ExceptionNotDeserializable, ex);
                throw;
            }
        }

        public object Convert(object value, TypeCode typeCode) => typeCode switch
        {
            TypeCode.Object => new object(),
            _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
        };

        public bool ToBoolean(object value) => this.CreateReader(value).ReadBoolean();

        public byte ToByte(object value) => this.CreateReader(value).ReadByte();

        public char ToChar(object value) => this.CreateReader(value).ReadChar();

        public DateTime ToDateTime(object value) => this.CreateReader(value).ReadDateTime();

        public decimal ToDecimal(object value) => formatter.UserDataSerializer.Deserialize((RawMessagePack)value, PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc.Default.Decimal);

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

        [GenerateShapeFor<bool>]
        [GenerateShapeFor<char>]
        [GenerateShapeFor<byte>]
        [GenerateShapeFor<sbyte>]
        [GenerateShapeFor<ushort>]
        [GenerateShapeFor<short>]
        [GenerateShapeFor<uint>]
        [GenerateShapeFor<int>]
        [GenerateShapeFor<ulong>]
        [GenerateShapeFor<long>]
        [GenerateShapeFor<float>]
        [GenerateShapeFor<double>]
        [GenerateShapeFor<string>]
        [GenerateShapeFor<decimal>]
        [GenerateShapeFor<DateTime>]
        [GenerateShapeFor<Dictionary<string, object>>]
        private partial class Witness;
    }
}
