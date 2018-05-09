// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Microsoft;

    /// <summary>
    /// Common RPC method transform functions that may be supplied to <see cref="JsonRpc.AddLocalRpcTarget(object, System.Func{string, string})"/>.
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
                    if (name == null)
                    {
                        throw new ArgumentNullException();
                    }

                    if (name.Length == 0)
                    {
                        return string.Empty;
                    }

                    // The remainder of this function is taken from https://github.com/JamesNK/Newtonsoft.Json/blob/94a4dbf7fe9aca9ce5ee1039dc44dfd8353bf17c/Src/Newtonsoft.Json/Utilities/StringUtils.cs#L151-L187
                    // with a few alterations.
                    if (!char.IsUpper(name[0]))
                    {
                        return name;
                    }

                    char[] chars = name.ToCharArray();

                    for (int i = 0; i < chars.Length; i++)
                    {
                        if (i == 1 && !char.IsUpper(chars[i]))
                        {
                            break;
                        }

                        bool hasNext = i + 1 < chars.Length;
                        if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                        {
                            // if the next character is a space, which is not considered uppercase
                            // (otherwise we wouldn't be here...)
                            // we want to ensure that the following:
                            // 'FOO bar' is rewritten as 'foo bar', and not as 'foO bar'
                            // The code was written in such a way that the first word in uppercase
                            // ends when if finds an uppercase letter followed by a lowercase letter.
                            // now a ' ' (space, (char)32) is considered not upper
                            // but in that case we still want our current character to become lowercase
                            if (char.IsSeparator(chars[i + 1]))
                            {
                                chars[i] = char.ToLowerInvariant(chars[i]);
                            }

                            break;
                        }

                        chars[i] = char.ToLowerInvariant(chars[i]);
                    }

                    return new string(chars);
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

            return name => prefix + name;
        }
    }
}
