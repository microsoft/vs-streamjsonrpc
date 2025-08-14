// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Helper methods for message formatter tracker classes.
/// </summary>
internal static class TrackerHelpers
{
    /// <summary>
    /// Dictionary to record the calculation made in <see cref="FindIProgressInterfaceImplementedBy(Type)"/> to obtain the IProgress{T} type from a given <see cref="Type"/>.
    /// </summary>
    private static readonly Dictionary<Type, Type?> TypeToIProgressMap = new();

    /// <summary>
    /// Dictionary to record the calculation made in <see cref="FindIAsyncEnumerableInterfaceImplementedBy(Type)"/> to obtain the IAsyncEnumerable{T} type from a given <see cref="Type"/>.
    /// </summary>
    private static readonly Dictionary<Type, Type?> TypeToIAsyncEnumerableMap = new();

    /// <summary>
    /// Extracts the IProgress{T} interface from a given <see cref="Type"/>, if it is implemented.
    /// </summary>
    /// <param name="objectType">The type which may implement IProgress{T}.</param>
    /// <returns>The IProgress{T} type from given <see cref="Type"/> object, or <see langword="null"/>  if no such interface was found in the given <paramref name="objectType" />.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern", Justification = "The 'IProgress<>' Type must exist and so trimmer kept it. In which case It also kept it on any type which implements it. The below call to GetInterfaces may return fewer results when trimmed but it will return 'IProgress<>' if the type implemented it, even after trimming.")]
    internal static Type? FindIProgressInterfaceImplementedBy(Type objectType)
    {
        Requires.NotNull(objectType, nameof(objectType));

        if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
        {
            return objectType;
        }

        Type? interfaceFromType;
        lock (TypeToIProgressMap)
        {
            if (!TypeToIProgressMap.TryGetValue(objectType, out interfaceFromType))
            {
                interfaceFromType = objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IProgress<>));
                TypeToIProgressMap.Add(objectType, interfaceFromType);
            }
        }

        return interfaceFromType;
    }

    /// <summary>
    /// Checks whether the given type is the IProgress{T} interface.
    /// </summary>
    /// <param name="objectType">The type to check.</param>
    /// <returns><see langword="true"/> if <paramref name="objectType"/> is a closed generic form of IProgress{T}; <see langword="false"/> otherwise.</returns>
    internal static bool IsIProgress(Type objectType) => Requires.NotNull(objectType, nameof(objectType)).IsConstructedGenericType && objectType.GetGenericTypeDefinition() == typeof(IProgress<>);

    /// <summary>
    /// Extracts the IAsyncEnumerable{T} interface from a given <see cref="Type"/>, if it is implemented.
    /// </summary>
    /// <param name="objectType">The type which may implement IAsyncEnumerable{T}.</param>
    /// <returns>The IAsyncEnumerable{T} type from given <see cref="Type"/> object, or <see langword="null"/>  if no such interface was found in the given <paramref name="objectType" />.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern", Justification = "The 'IAsyncEnumerable<>' Type must exist and so trimmer kept it. In which case It also kept it on any type which implements it. The below call to GetInterfaces may return fewer results when trimmed but it will return 'IAsyncEnumerable<>' if the type implemented it, even after trimming.")]
    internal static Type? FindIAsyncEnumerableInterfaceImplementedBy(Type objectType)
    {
        Requires.NotNull(objectType, nameof(objectType));

        if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(typeof(IAsyncEnumerable<>)))
        {
            return objectType;
        }

        Type? interfaceFromType;
        lock (TypeToIAsyncEnumerableMap)
        {
            if (!TypeToIAsyncEnumerableMap.TryGetValue(objectType, out interfaceFromType))
            {
                interfaceFromType = objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
                TypeToIAsyncEnumerableMap.Add(objectType, interfaceFromType);
            }
        }

        return interfaceFromType;
    }

    /// <summary>
    /// Checks whether the given type is the IAsyncEnumerable{T} interface.
    /// </summary>
    /// <param name="objectType">The type to check.</param>
    /// <returns><see langword="true"/> if <paramref name="objectType"/> is a closed generic form of IAsyncEnumerable{T}; <see langword="false"/> otherwise.</returns>
    internal static bool IsIAsyncEnumerable(Type objectType) => Requires.NotNull(objectType, nameof(objectType)).IsConstructedGenericType && objectType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);
}
