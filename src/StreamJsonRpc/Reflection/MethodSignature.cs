// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Microsoft;

    [DebuggerDisplay("{DebuggerDisplay}")]
    internal sealed class MethodSignature : IEquatable<MethodSignature>
    {
        private static readonly StringComparer TypeNameComparer = StringComparer.Ordinal;

        /// <summary>
        /// Backing field for the lazily initialized <see cref="Parameters"/> property.
        /// </summary>
        private ParameterInfo[]? parameters;

        internal MethodSignature(MethodInfo methodInfo, JsonRpcMethodAttribute? attribute)
        {
            Requires.NotNull(methodInfo, nameof(methodInfo));
            this.MethodInfo = methodInfo;
            this.Attribute = attribute;
        }

        internal MethodInfo MethodInfo { get; }

        internal JsonRpcMethodAttribute? Attribute { get; }

        internal ParameterInfo[] Parameters => this.parameters ?? (this.parameters = this.MethodInfo.GetParameters() ?? Array.Empty<ParameterInfo>());

        internal bool IsPublic => this.MethodInfo.IsPublic;

        internal string Name => this.MethodInfo.Name;

        internal int RequiredParamCount => this.Parameters.Count(pi => !pi.IsOptional && !IsCancellationToken(pi));

        internal int TotalParamCountExcludingCancellationToken => this.HasCancellationTokenParameter ? this.Parameters.Length - 1 : this.Parameters.Length;

        internal bool HasCancellationTokenParameter => this.Parameters.Length > 0 && this.Parameters[this.Parameters.Length - 1].ParameterType == typeof(CancellationToken);

        internal bool HasOutOrRefParameters => this.Parameters.Any(pi => pi.IsOut || pi.ParameterType.IsByRef);

        [ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{this.MethodInfo.DeclaringType}.{this.Name}({string.Join(", ", this.Parameters.Select(p => p.ParameterType.Name))})";

        /// <inheritdoc/>
        public bool Equals(MethodSignature? other)
        {
            if (other is null)
            {
                return false;
            }

            if (object.ReferenceEquals(other, this) || object.ReferenceEquals(this.Parameters, other.Parameters))
            {
                return true;
            }

            if (this.Parameters.Length != other.Parameters.Length)
            {
                return false;
            }

            for (int index = 0; index < this.Parameters.Length; index++)
            {
                if (!MethodSignature.TypeNameComparer.Equals(
                        this.Parameters[index].ParameterType.AssemblyQualifiedName,
                        other.Parameters[index].ParameterType.AssemblyQualifiedName))
                {
                    return false;
                }
            }

            // We intentionally omit equating the MethodInfo itself because we want to consider
            // overrides to be equal across types in the type hierarchy.
            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is MethodSignature other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            uint result = 0;
            int bitCount = sizeof(uint) * 8;
            const int shift = 1;

            foreach (ParameterInfo parameter in this.Parameters)
            {
                // Shifting result 1 bit per each parameter so that the hash is different for
                // methods with the same parameter types at different location, e.g.
                // foo(int, string) and foo(string, int)
                // This will work fine for up to 32 (64 on x64) parameters,
                // which should be more than enough for the most applications.
                result = result << shift | result >> (bitCount - shift);
                result ^= (uint)MethodSignature.TypeNameComparer.GetHashCode(parameter.ParameterType.AssemblyQualifiedName!);
            }

            return (int)result;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return this.DebuggerDisplay;
        }

        internal bool MatchesParametersExcludingCancellationToken(ReadOnlySpan<ParameterInfo> parameters)
        {
            if (this.TotalParamCountExcludingCancellationToken == parameters.Length)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType != this.Parameters[i].ParameterType)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        private static bool IsCancellationToken(ParameterInfo parameter) => parameter?.ParameterType.Equals(typeof(CancellationToken)) ?? false;
    }
}
