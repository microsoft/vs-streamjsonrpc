// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;

namespace StreamJsonRpc;

/// <summary>
/// Common RPC method transform functions that may be supplied to <see cref="JsonRpc.AddLocalRpcTarget(object, JsonRpcTargetOptions)"/>
/// by way of <see cref="JsonRpcTargetOptions.MethodNameTransform"/>.
/// </summary>
public static class CommonMethodNameTransforms
{
    /// <summary>
    /// Gets a function that converts a given string from PascalCase to camelCase.
    /// </summary>
    public static Func<string, string> CamelCase
    {
        get
        {
            return name =>
            {
                if (name is null)
                {
                    throw new ArgumentNullException();
                }

                return Utilities.ToCamelCase(name);
            };
        }
    }

    /// <summary>
    /// Gets a function that prepends a particular string in front of any RPC method name.
    /// </summary>
    /// <param name="prefix">
    /// The prefix to prepend to any method name.
    /// This value must not be null.
    /// When this value is the empty string, no transformation is performed by the returned function.
    /// </param>
    /// <returns>The transform function.</returns>
    public static Func<string, string> Prepend(string prefix)
    {
        Requires.NotNull(prefix, nameof(prefix));

        if (prefix.Length == 0)
        {
            return name => name;
        }
        else
        {
            // Using a local variable for the closure avoids C# from allocating the closure
            // earlier in the method, which would impact even the fast path.
            string localPrefix = prefix;
            return name => localPrefix + name;
        }
    }
}
