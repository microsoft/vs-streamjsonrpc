// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;

namespace StreamJsonRpc;

/// <summary>
/// Serves as a wrapper for a named arguments object, allowing access to its properties and fields by name.
/// </summary>
/// <remarks>
/// This type implements <see cref="IReadOnlyDictionary{TKey, TValue}"/> to allow access to the named arguments by their names.
/// It also exposes the declared types of the named arguments via its <see cref="DeclaredArgumentTypes"/> property, which can be useful for serialization.
/// </remarks>
public class NamedArgs : IReadOnlyDictionary<string, object?>
{
    private static readonly ConcurrentDictionary<Type, (IReadOnlyDictionary<string, Type> Types, IReadOnlyDictionary<string, Func<object, object?>> Readers)> Arguments = [];

    private readonly IReadOnlyDictionary<string, Type> types;
    private readonly IReadOnlyDictionary<string, Func<object, object?>> readers;
    private readonly object namedArgsObject;

    internal NamedArgs(IReadOnlyDictionary<string, Type> declaredArgumentTypes, IReadOnlyDictionary<string, Func<object, object?>> argumentReaders, object namedArgsObject)
    {
        this.types = declaredArgumentTypes;
        this.readers = argumentReaders;
        this.namedArgsObject = namedArgsObject;
    }

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => this.readers.Keys;

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => this.readers.Select(r => r.Value(this.namedArgsObject));

    int IReadOnlyCollection<KeyValuePair<string, object?>>.Count => this.readers.Count;

    /// <summary>
    /// Gets the declared types of the named arguments.
    /// </summary>
    public IReadOnlyDictionary<string, Type> DeclaredArgumentTypes => this.types;

    object? IReadOnlyDictionary<string, object?>.this[string key] => this.readers[key](this.namedArgsObject);

    /// <summary>
    /// Creates a new instance of the <see cref="NamedArgs"/> class from the specified object.
    /// </summary>
    /// <typeparam name="T"><inheritdoc cref="Create(Type, object?)" path="/param[@name='objectType']"/></typeparam>
    /// <param name="namedArgsObject"><inheritdoc cref="Create(Type, object?)" path="/param[@name='namedArgsObject']"/></param>
    /// <returns><inheritdoc cref="Create(Type, object?)" path="/returns"/></returns>
    [return: NotNullIfNotNull(nameof(namedArgsObject))]
    public static NamedArgs? Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.NonPublicFields)] T>(T? namedArgsObject)
        => Create(typeof(T), namedArgsObject);

    /// <summary>
    /// Creates a <see cref="NamedArgs"/> instance for the specified named arguments object.
    /// </summary>
    /// <param name="objectType">
    /// The type of the <paramref name="namedArgsObject"/>. Must not be <see cref="object"/>.
    /// This is the type whose properties and fields will be used as named arguments.
    /// If <see cref="DataContractAttribute"/> is applied to this type, properties and fields will be included only if <see cref="DataMemberAttribute"/> is applied to them, and may include non-public members.
    /// If <see cref="DataContractAttribute"/> is not applied, only public properties and fields will be included, and any <see cref="IgnoreDataMemberAttribute"/> annotated members will be excluded.
    /// </param>
    /// <param name="namedArgsObject">The object whose properties or fields name the arguments to pass to the RPC method.</param>
    /// <returns>An optimized <see cref="NamedArgs"/> object, or <see langword="null" /> if <paramref name="namedArgsObject"/> was <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="namedArgsObject"/> is not assignable to <paramref name="objectType"/>, or <paramref name="objectType"/> is <see cref="object"/>.</exception>
    [return: NotNullIfNotNull(nameof(namedArgsObject))]
    public static NamedArgs? Create([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.NonPublicFields)] Type objectType, object? namedArgsObject)
    {
        Requires.NotNull(objectType);

        if (namedArgsObject is null)
        {
            return null;
        }

        if (!objectType.IsAssignableFrom(namedArgsObject.GetType()))
        {
            throw new ArgumentException("The arguments object must be assignable to the given type.", nameof(namedArgsObject));
        }

        if (objectType == typeof(object))
        {
            throw new ArgumentException("The object type must not be System.Object.", nameof(objectType));
        }

        (IReadOnlyDictionary<string, Type> types, IReadOnlyDictionary<string, Func<object, object?>> readers) = Arguments.GetOrAdd(objectType, AnalyzeMembers);
        return new NamedArgs(types, readers, namedArgsObject);
    }

    bool IReadOnlyDictionary<string, object?>.ContainsKey(string key) => this.readers.ContainsKey(key);

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() => this.readers.Select(r => new KeyValuePair<string, object?>(r.Key, r.Value(this.namedArgsObject))).GetEnumerator();

    bool IReadOnlyDictionary<string, object?>.TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        if (this.readers.TryGetValue(key, out Func<object, object?>? reader))
        {
            value = reader(this.namedArgsObject);
            return true;
        }
        else
        {
            value = null;
            return false;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        IEnumerable<KeyValuePair<string, object?>> enumerable = this;
        return enumerable.GetEnumerator();
    }

    private static (IReadOnlyDictionary<string, Type> Types, IReadOnlyDictionary<string, Func<object, object?>> Readers) AnalyzeMembers([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.NonPublicFields)] Type type)
    {
        Dictionary<string, Type>? types = new(StringComparer.Ordinal);
        Dictionary<string, Func<object, object?>>? readers = new(StringComparer.Ordinal);

        bool isDataContract = type.GetCustomAttribute<DataContractAttribute>() is not null;

        BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
        if (isDataContract)
        {
            bindingFlags |= BindingFlags.NonPublic;
        }

        foreach (PropertyInfo property in type.GetProperties(bindingFlags))
        {
            if (property.GetMethod is not null)
            {
                if (TryGetSerializationInfo(property, out string key))
                {
                    types[key] = property.PropertyType;
                    readers[key] = target => property.GetValue(target);
                }
            }
        }

        foreach (FieldInfo field in type.GetFields(bindingFlags))
        {
            if (TryGetSerializationInfo(field, out string key))
            {
                types[key] = field.FieldType;
                readers[key] = target => field.GetValue(target);
            }
        }

        return (types, readers);

        bool TryGetSerializationInfo(MemberInfo memberInfo, out string key)
        {
            key = memberInfo.Name;
            if (isDataContract)
            {
                DataMemberAttribute? dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                if (dataMemberAttribute is null)
                {
                    return false;
                }

                if (!dataMemberAttribute.EmitDefaultValue)
                {
                    throw new NotSupportedException($"(DataMemberAttribute.EmitDefaultValue == false) is not supported but was found on: {memberInfo.DeclaringType!.FullName}.{memberInfo.Name}.");
                }

                key = dataMemberAttribute.Name ?? memberInfo.Name;
                return true;
            }
            else
            {
                return memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() is null;
            }
        }
    }
}
