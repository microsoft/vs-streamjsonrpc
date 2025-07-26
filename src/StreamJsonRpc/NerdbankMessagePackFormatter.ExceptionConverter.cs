﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType.Abstractions;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// Manages serialization of any <see cref="Exception"/>-derived type that follows standard <see cref="SerializableAttribute"/> rules.
    /// </summary>
    /// <remarks>
    /// A serializable class will:
    /// 1. Derive from <see cref="Exception"/>
    /// 2. Be attributed with <see cref="SerializableAttribute"/>
    /// 3. Declare a constructor with a signature of (<see cref="SerializationInfo"/>, <see cref="StreamingContext"/>).
    /// </remarks>
    private class ExceptionConverter<T> : MessagePackConverter<T>
    {
        public static readonly ExceptionConverter<T> Instance = new();

        public override T? Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            Assumes.NotNull(formatter.JsonRpc);

            context.DepthStep();

            if (reader.TryReadNil())
            {
                return default;
            }

            // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
            // Each exception in the russian doll is a new serialization job from its perspective.
            formatter.exceptionRecursionCounter.Value++;
            try
            {
                if (formatter.exceptionRecursionCounter.Value > formatter.JsonRpc.ExceptionOptions.RecursionLimit)
                {
                    // Exception recursion has gone too deep. Skip this value and return null as if there were no inner exception.
                    // Note that in skipping, the parser may use recursion internally and may still throw if its own limits are exceeded.
                    reader.Skip(context);
                    return default;
                }

                var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(formatter));
                int memberCount = reader.ReadMapHeader();
                for (int i = 0; i < memberCount; i++)
                {
                    string? name = context.GetConverter<string>(context.TypeShapeProvider).Read(ref reader, context)
                        ?? throw new MessagePackSerializationException(Resources.UnexpectedNullValueInMap);

                    // SerializationInfo.GetValue(string, typeof(object)) does not call our formatter,
                    // so the caller will get a boxed RawMessagePack struct in that case.
                    // Although we can't do much about *that* in general, we can at least ensure that null values
                    // are represented as null instead of this boxed struct.
                    RawMessagePack? value = reader.TryReadNil() ? null : reader.ReadRaw(context);

                    info.AddSafeValue(name, value);
                }

                return ExceptionSerializationHelpers.Deserialize<T>(formatter.JsonRpc, info, formatter.JsonRpc, formatter.JsonRpc.TraceSource);
            }
            finally
            {
                formatter.exceptionRecursionCounter.Value--;
            }
        }

        public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            formatter.exceptionRecursionCounter.Value++;
            try
            {
                if (formatter.exceptionRecursionCounter.Value > formatter.JsonRpc?.ExceptionOptions.RecursionLimit)
                {
                    // Exception recursion has gone too deep. Skip this value and write null as if there were no inner exception.
                    writer.WriteNil();
                    return;
                }

                var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(formatter));
                ExceptionSerializationHelpers.Serialize((Exception)(object)value, info);
                writer.WriteMapHeader(info.GetSafeMemberCount());
                foreach (SerializationEntry element in info.GetSafeMembers())
                {
                    writer.Write(element.Name);
                    if (element.Value is null)
                    {
                        writer.WriteNil();
                    }
                    else
                    {
                        // We prefer the declared type but will fallback to the runtime type.
                        context.GetConverter(formatter.TypeShapeProvider.GetShape(NormalizeType(element.ObjectType)) ?? formatter.TypeShapeProvider.Resolve(NormalizeType(element.Value.GetType())))
                            .WriteObject(ref writer, element.Value, context);
                    }
                }
            }
            finally
            {
                formatter.exceptionRecursionCounter.Value--;
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }
}
