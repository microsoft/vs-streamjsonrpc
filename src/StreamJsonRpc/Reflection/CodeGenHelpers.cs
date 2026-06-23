// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Helper methods for dynamically generated proxies to invoke.
/// This type is only public because mono does not support IgnoresAccessChecksToAttribute. Do not call directly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CodeGenHelpers
{
    private static readonly ConditionalWeakTable<Func<string, string>, ConcurrentDictionary<IReadOnlyList<string>, int>> ParameterNameTransformStateCache = new();

#pragma warning disable VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
    /// <inheritdoc cref="ProxyGeneration.AsyncEnumerableProxy{T}"/>
    public static IAsyncEnumerable<T> CreateAsyncEnumerableProxy<T>(Task<IAsyncEnumerable<T>> enumerableTask, CancellationToken defaultCancellationToken) => new ProxyGeneration.AsyncEnumerableProxy<T>(enumerableTask, defaultCancellationToken);
#pragma warning restore VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.

    /// <summary>
    /// Creates a named arguments dictionary from raw parameter names and values.
    /// </summary>
    public static Dictionary<string, object?> CreateNamedArguments(Func<string, string> parameterNameTransform, IReadOnlyList<string> parameterNames, IReadOnlyList<object?> argumentValues)
    {
        Requires.NotNull(parameterNameTransform, nameof(parameterNameTransform));
        Requires.NotNull(parameterNames, nameof(parameterNames));
        Requires.NotNull(argumentValues, nameof(argumentValues));
        Requires.Argument(parameterNames.Count == argumentValues.Count, nameof(argumentValues), "Argument and parameter name counts must match.");

        var result = new Dictionary<string, object?>(parameterNames.Count, StringComparer.Ordinal);
        for (int i = 0; i < parameterNames.Count; i++)
        {
            string transformedName = parameterNameTransform(parameterNames[i]);
            Requires.Argument(transformedName is not null, nameof(parameterNameTransform), "Delegate returned a null parameter name.");
            result.Add(transformedName, argumentValues[i]);
        }

        return result;
    }

    /// <summary>
    /// Creates a named-argument type dictionary from raw parameter names and declared parameter types.
    /// </summary>
    public static Dictionary<string, Type> CreateNamedArgumentDeclaredTypes(Func<string, string> parameterNameTransform, IReadOnlyList<string> parameterNames, IReadOnlyList<Type> parameterTypes)
    {
        Requires.NotNull(parameterNameTransform, nameof(parameterNameTransform));
        Requires.NotNull(parameterNames, nameof(parameterNames));
        Requires.NotNull(parameterTypes, nameof(parameterTypes));
        Requires.Argument(parameterNames.Count == parameterTypes.Count, nameof(parameterTypes), "Parameter name and type counts must match.");

        var result = new Dictionary<string, Type>(parameterNames.Count, StringComparer.Ordinal);
        for (int i = 0; i < parameterNames.Count; i++)
        {
            string transformedName = parameterNameTransform(parameterNames[i]);
            Requires.Argument(transformedName is not null, nameof(parameterNameTransform), "Delegate returned a null parameter name.");
            result.Add(transformedName, parameterTypes[i]);
        }

        return result;
    }

    /// <summary>
    /// Checks whether applying the transform preserves all parameter names.
    /// </summary>
    public static bool AreParameterNamesUnchanged(Func<string, string> parameterNameTransform, IReadOnlyList<string> parameterNames)
    {
        Requires.NotNull(parameterNameTransform, nameof(parameterNameTransform));
        Requires.NotNull(parameterNames, nameof(parameterNames));

        foreach (string name in parameterNames)
        {
            if (!StringComparer.Ordinal.Equals(name, parameterNameTransform(name)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the parameter-name transform state for a method.
    /// </summary>
    /// <param name="parameterNameTransform">The transform to evaluate.</param>
    /// <param name="parameterNames">The CLR/attribute parameter names for the method.</param>
    /// <returns>
    /// <c>1</c> when no transformed names are required; <c>2</c> when transformed names are required.
    /// </returns>
    public static int GetParameterNameTransformState(Func<string, string> parameterNameTransform, IReadOnlyList<string> parameterNames)
    {
        Requires.NotNull(parameterNameTransform, nameof(parameterNameTransform));
        Requires.NotNull(parameterNames, nameof(parameterNames));

        if (ReferenceEquals(parameterNameTransform, JsonRpcProxyOptions.Default.ParameterNameTransform))
        {
            return 1;
        }

        ConcurrentDictionary<IReadOnlyList<string>, int> byParameterNames = ParameterNameTransformStateCache.GetValue(parameterNameTransform, static _ => new());
        return byParameterNames.GetOrAdd(parameterNames, names => AreParameterNamesUnchanged(parameterNameTransform, names) ? 1 : 2);
    }
}
