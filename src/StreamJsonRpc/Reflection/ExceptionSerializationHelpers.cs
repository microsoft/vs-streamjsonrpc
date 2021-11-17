// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;

    internal static class ExceptionSerializationHelpers
    {
        /// <summary>
        /// The name of the value stored by exceptions that stores watson bucket information.
        /// </summary>
        /// <remarks>
        /// This value should be suppressed when writing or reading exceptions as it is irrelevant to
        /// remote parties and otherwise adds to the size to the payload.
        /// </remarks>
        internal const string WatsonBucketsKey = "WatsonBuckets";

        private const string AssemblyNameKeyName = "AssemblyName";

        private static readonly Type[] DeserializingConstructorParameterTypes = new Type[] { typeof(SerializationInfo), typeof(StreamingContext) };

        private static StreamingContext Context => new StreamingContext(StreamingContextStates.Remoting);

        internal static T Deserialize<T>(JsonRpc jsonRpc, SerializationInfo info, TraceSource? traceSource)
            where T : Exception
        {
            if (!TryGetValue(info, "ClassName", out string? runtimeTypeName) || runtimeTypeName is null)
            {
                throw new NotSupportedException("ClassName was not found in the serialized data.");
            }

            TryGetValue(info, AssemblyNameKeyName, out string? runtimeAssemblyName);
            Type? runtimeType = jsonRpc.LoadType(runtimeTypeName, runtimeAssemblyName);
            if (runtimeType is null)
            {
                if (traceSource?.Switch.ShouldTrace(TraceEventType.Warning) ?? false)
                {
                    traceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.ExceptionTypeNotFound, "{0} type could not be loaded. Falling back to System.Exception.", runtimeTypeName);
                }

                // fallback to deserializing the base Exception type.
                runtimeType = typeof(Exception);
            }

            // Sanity/security check: ensure the runtime type derives from the expected type.
            if (!typeof(T).IsAssignableFrom(runtimeType))
            {
                throw new NotSupportedException($"{runtimeTypeName} does not derive from {typeof(T).FullName}.");
            }

            // Find the nearest exception type that implements the deserializing constructor and is deserializable.
            ConstructorInfo? ctor = null;
            Type? originalRuntimeType = runtimeType;
            while (runtimeType is object)
            {
                string? errorMessage =
                    runtimeType.GetCustomAttribute<SerializableAttribute>() is null ? $"{runtimeType.FullName} is not annotated with a {nameof(SerializableAttribute)}." :
                    !jsonRpc.ExceptionOptions.CanDeserialize(runtimeType) ? $"{runtimeType.FullName} is not an allowed type to deserialize." :
                    (ctor = FindDeserializingConstructor(runtimeType)) is null ? $"{runtimeType.FullName} does not declare a deserializing constructor with signature ({string.Join(", ", DeserializingConstructorParameterTypes.Select(t => t.FullName))})." :
                    null;
                if (errorMessage is null)
                {
                    break;
                }

                if (runtimeType.BaseType is Type baseType)
                {
                    errorMessage += $" {baseType.FullName} will be deserialized instead, if possible.";
                }

                traceSource?.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.ExceptionNotDeserializable, errorMessage);

                runtimeType = runtimeType.BaseType;
            }

            if (ctor is null)
            {
                throw new NotSupportedException($"{originalRuntimeType.FullName} is not a supported exception type to deserialize and no adequate substitute could be found.");
            }

            return (T)ctor.Invoke(new object?[] { info, Context });
        }

        internal static void Serialize(Exception exception, SerializationInfo info)
        {
            Type exceptionType = exception.GetType();
            EnsureSerializableAttribute(exceptionType);
            exception.GetObjectData(info, Context);
            info.AddValue(AssemblyNameKeyName, exception.GetType().Assembly.FullName);
        }

        internal static bool IsSerializable(Exception exception) => exception.GetType().GetCustomAttribute<SerializableAttribute>() is object;

        internal static object Convert(IFormatterConverter formatterConverter, object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Boolean => formatterConverter.ToBoolean(value),
                TypeCode.Byte => formatterConverter.ToBoolean(value),
                TypeCode.Char => formatterConverter.ToChar(value),
                TypeCode.DateTime => formatterConverter.ToDateTime(value),
                TypeCode.Decimal => formatterConverter.ToDecimal(value),
                TypeCode.Double => formatterConverter.ToDouble(value),
                TypeCode.Int16 => formatterConverter.ToInt16(value),
                TypeCode.Int32 => formatterConverter.ToInt32(value),
                TypeCode.Int64 => formatterConverter.ToInt64(value),
                TypeCode.SByte => formatterConverter.ToSByte(value),
                TypeCode.Single => formatterConverter.ToSingle(value),
                TypeCode.String => formatterConverter.ToString(value)!,
                TypeCode.UInt16 => formatterConverter.ToUInt16(value),
                TypeCode.UInt32 => formatterConverter.ToUInt32(value),
                TypeCode.UInt64 => formatterConverter.ToUInt64(value),
                _ => throw new NotSupportedException("Unsupported type code: " + typeCode),
            };
        }

        /// <summary>
        /// Gets a value like <see cref="SerializationInfo.MemberCount"/>
        /// but omits members that should not be serialized.
        /// </summary>
        internal static int GetSafeMemberCount(this SerializationInfo info) => info.GetSafeMembers().Count();

        /// <summary>
        /// Gets a member enumerator that omits members that should not be serialized.
        /// </summary>
        internal static IEnumerable<SerializationEntry> GetSafeMembers(this SerializationInfo info)
        {
            foreach (SerializationEntry element in info)
            {
                if (element.Name != WatsonBucketsKey)
                {
                    yield return element;
                }
            }
        }

        /// <summary>
        /// Adds a member if it isn't among those that should not be deserialized.
        /// </summary>
        internal static void AddSafeValue(this SerializationInfo info, string name, object? value)
        {
            if (name != WatsonBucketsKey)
            {
                info.AddValue(name, value);
            }
        }

        private static void EnsureSerializableAttribute(Type runtimeType)
        {
            if (runtimeType.GetCustomAttribute<SerializableAttribute>() is null)
            {
                throw new NotSupportedException($"{runtimeType.FullName} is not marked with the {typeof(SerializableAttribute).FullName}.");
            }
        }

        private static ConstructorInfo? FindDeserializingConstructor(Type runtimeType) => runtimeType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, DeserializingConstructorParameterTypes, null);

        private static bool TryGetValue(SerializationInfo info, string key, out string? value)
        {
            try
            {
                value = info.GetString(key);
                return true;
            }
            catch (SerializationException)
            {
                value = null;
                return false;
            }
        }
    }
}
