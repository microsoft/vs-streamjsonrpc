// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Microsoft;

    /// <summary>
    /// Helper methods for message formatter tracker classes.
    /// </summary>
    /// <typeparam name="TInterface">A generic interface. We only need the generic type definition, but since C# doesn't let us pass in open generic types, use <see cref="int"/> as a generic type argument.</typeparam>
    internal static class TrackerHelpers<TInterface>
        where TInterface : class
    {
        /// <summary>
        /// Dictionary to record the calculation made in <see cref="FindInterfaceImplementedBy(Type)"/> to obtain the <typeparamref name="TInterface"/> type from a given <see cref="Type"/>.
        /// </summary>
        private static readonly Dictionary<Type, Type> TypeToImplementedInterfaceMap = new Dictionary<Type, Type>();

        /// <summary>
        /// Gets the generic type definition for whatever type parameter was given by <typeparamref name="TInterface" />.
        /// </summary>
        private static readonly Type InterfaceGenericTypeDefinition = typeof(TInterface).GetGenericTypeDefinition();

        /// <summary>
        /// Extracts some interface from a given <see cref="Type"/>, if it is implemented.
        /// </summary>
        /// <param name="objectType">The type which may implement <typeparamref name="TInterface"/>.</param>
        /// <returns>The <typeparamref name="TInterface"/> type from given <see cref="Type"/> object, or <c>null</c>  if no such interface was found in the given <paramref name="objectType" />.</returns>
        internal static Type? FindInterfaceImplementedBy(Type objectType)
        {
            Requires.NotNull(objectType, nameof(objectType));

            if (objectType.IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(InterfaceGenericTypeDefinition))
            {
                return objectType;
            }

            Type? interfaceFromType;
            lock (TypeToImplementedInterfaceMap)
            {
                if (!TypeToImplementedInterfaceMap.TryGetValue(objectType, out interfaceFromType))
                {
                    interfaceFromType = objectType.GetTypeInfo().GetInterfaces().FirstOrDefault(i => i.IsConstructedGenericType && i.GetGenericTypeDefinition() == InterfaceGenericTypeDefinition);
                    TypeToImplementedInterfaceMap.Add(objectType, interfaceFromType);
                }
            }

            return interfaceFromType;
        }

        /// <summary>
        /// Checks if a given <see cref="Type"/> implements <typeparamref name="TInterface"/>.
        /// </summary>
        /// <param name="objectType">The type which may implement <typeparamref name="TInterface"/>.</param>
        /// <returns>true if given <see cref="Type"/> implements <typeparamref name="TInterface"/>; otherwise, false.</returns>
        internal static bool CanSerialize(Type objectType) => FindInterfaceImplementedBy(objectType) != null;

        /// <summary>
        /// Checks whether the given type is an interface compatible with <typeparamref name="TInterface"/>.
        /// </summary>
        /// <param name="objectType">The type that may be deserialized.</param>
        /// <returns><c>true</c> if <paramref name="objectType"/> is a closed generic form of <typeparamref name="TInterface"/>; <c>false</c> otherwise.</returns>
        internal static bool CanDeserialize(Type objectType) => IsActualInterfaceMatch(objectType);

        /// <summary>
        /// Checks whether the given type is an interface compatible with <typeparamref name="TInterface"/>.
        /// </summary>
        /// <param name="objectType">The type that may be deserialized.</param>
        /// <returns><c>true</c> if <paramref name="objectType"/> is a closed generic form of <typeparamref name="TInterface"/>; <c>false</c> otherwise.</returns>
        internal static bool IsActualInterfaceMatch(Type objectType) => Requires.NotNull(objectType, nameof(objectType)).IsConstructedGenericType && objectType.GetGenericTypeDefinition().Equals(InterfaceGenericTypeDefinition);
    }
}
