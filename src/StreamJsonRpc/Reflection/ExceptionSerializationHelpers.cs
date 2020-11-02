// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;

    internal static class ExceptionSerializationHelpers
    {
        private const string AssemblyNameKeyName = "AssemblyName";

        private static readonly Type[] DeserializingConstructorParameterTypes = new Type[] { typeof(SerializationInfo), typeof(StreamingContext) };

        private static StreamingContext Context => new StreamingContext(StreamingContextStates.Remoting);

        internal static T Deserialize<T>(SerializationInfo info, TraceSource? traceSource)
            where T : Exception
        {
            if (!TryGetValue(info, "ClassName", out string? runtimeTypeName) || runtimeTypeName is null)
            {
                throw new NotSupportedException("ClassName was not found in the serialized data.");
            }

            Assembly? exceptionDeclaringAssembly = null;
            if (TryGetValue(info, AssemblyNameKeyName, out string? runtimeAssemblyName) && runtimeAssemblyName is object)
            {
                try
                {
                    exceptionDeclaringAssembly = Assembly.Load(runtimeAssemblyName);
                }
                catch (System.IO.FileLoadException)
                {
                    // Try removing the version from the AssemblyName and try again, in case the message came from a newer version.
                    var an = new AssemblyName(runtimeAssemblyName);
                    if (an.Version is object)
                    {
                        an.Version = null;
                        try
                        {
                            exceptionDeclaringAssembly = Assembly.Load(an.FullName);
                        }
                        catch (System.IO.FileLoadException)
                        {
                            // If we fail again, we'll just try to load the exception type from the AppDomain without an assembly's context.
                        }
                    }
                }
            }

            Type? runtimeType = exceptionDeclaringAssembly is object ? exceptionDeclaringAssembly.GetType(runtimeTypeName) : Type.GetType(runtimeTypeName);
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

            EnsureSerializableAttribute(runtimeType);

            ConstructorInfo? ctor = FindDeserializingConstructor(runtimeType);
            if (ctor is null)
            {
                throw new NotSupportedException($"{runtimeType.FullName} does not declare a deserializing constructor with signature ({string.Join(", ", DeserializingConstructorParameterTypes.Select(t => t.FullName))}).");
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
                TypeCode.String => formatterConverter.ToString(value),
                TypeCode.UInt16 => formatterConverter.ToUInt16(value),
                TypeCode.UInt32 => formatterConverter.ToUInt32(value),
                TypeCode.UInt64 => formatterConverter.ToUInt64(value),
                _ => throw new NotSupportedException("Unsupported type code: " + typeCode),
            };
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
