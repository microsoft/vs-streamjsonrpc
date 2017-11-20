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
        private static readonly ParameterInfo[] EmptyParameterInfoArray = new ParameterInfo[0];
        private static readonly StringComparer TypeNameComparer = StringComparer.Ordinal;

        /// <summary>
        /// Backing field for the lazily initialized <see cref="Parameters"/> property.
        /// </summary>
        private ParameterInfo[] parameters;

        internal MethodSignature(MethodInfo methodInfo)
        {
            Requires.NotNull(methodInfo, nameof(methodInfo));
            this.MethodInfo = methodInfo;
        }

        internal MethodInfo MethodInfo { get; }

        internal ParameterInfo[] Parameters => this.parameters ?? (this.parameters = this.MethodInfo.GetParameters() ?? EmptyParameterInfoArray);

        internal bool IsPublic => this.MethodInfo.IsPublic;

        internal string Name => this.MethodInfo.Name;

        internal int RequiredParamCount => this.Parameters.Count(pi => !pi.IsOptional && !IsCancellationToken(pi));

        internal int TotalParamCountExcludingCancellationToken => this.Parameters.Count(pi => !IsCancellationToken(pi));

        internal bool HasCancellationTokenParameter => this.Parameters.Any(IsCancellationToken);

        internal bool HasOutOrRefParameters => this.Parameters.Any(pi => pi.IsOut || pi.ParameterType.IsByRef);

        [ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{this.MethodInfo.DeclaringType}.{this.Name}({string.Join(", ", this.Parameters.Select(p => p.ParameterType.Name))})";

        bool IEquatable<MethodSignature>.Equals(MethodSignature other)
        {
            if (object.ReferenceEquals(other, null))
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

        public override bool Equals(object obj)
        {
            return (obj is MethodSignature) && ((IEquatable<MethodSignature>)this).Equals((MethodSignature)obj);
        }

        public override int GetHashCode()
        {
            uint result = 0;
            int bitCount = sizeof(uint) * 8;
            const int shift = 1;

            foreach (ParameterInfo parameter in this.MethodInfo.GetParameters())
            {
                // Shifting result 1 bit per each parameter so that the hash is different for
                // methods with the same parameter types at different location, e.g.
                // foo(int, string) and foo(string, int)
                // This will work fine for up to 32 (64 on x64) parameters,
                // which should be more than enough for the most applications.
                result = result << shift | result >> (bitCount - shift);
                result ^= (uint)MethodSignature.TypeNameComparer.GetHashCode(parameter.ParameterType.AssemblyQualifiedName);
            }

            return (int)result;
        }

        public override string ToString()
        {
            return this.DebuggerDisplay;
        }

        private static bool IsCancellationToken(ParameterInfo parameter) => parameter?.ParameterType.Equals(typeof(CancellationToken)) ?? false;
    }
}
