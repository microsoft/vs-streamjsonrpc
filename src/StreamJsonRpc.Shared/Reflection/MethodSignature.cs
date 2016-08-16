using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft;

namespace StreamJsonRpc
{
    internal sealed class MethodSignature : IEquatable<MethodSignature>
    {
        private static readonly StringComparer typeNameComparer = StringComparer.Ordinal;

        internal MethodSignature(MethodInfo methodInfo)
        {
            Requires.NotNull(methodInfo, nameof(methodInfo));
            this.MethodInfo = methodInfo;
            this.Parameters = methodInfo.GetParameters() ?? new ParameterInfo[0];
        }

        internal MethodInfo MethodInfo { get; }

        internal ParameterInfo[] Parameters { get; }

        internal bool IsPublic => this.MethodInfo.IsPublic;

        internal string Name => this.MethodInfo.Name;

        internal int RequiredParamCount => this.Parameters.Count(pi => !pi.IsOptional && !IsCancellationToken(pi));

        internal int TotalParamCountExcludingCancellationToken => this.Parameters.Count(pi => !IsCancellationToken(pi));

        internal bool HasCancellationTokenParameter => IsCancellationToken(this.Parameters.LastOrDefault());

        internal bool HasOutOrRefParameters => this.Parameters.Any(pi => pi.IsOut || pi.ParameterType.IsByRef);

        bool IEquatable<MethodSignature>.Equals(MethodSignature other)
        {
            if (Object.ReferenceEquals(other, null))
            {
                return false;
            }

            if (Object.ReferenceEquals(other, this) || Object.ReferenceEquals(this.Parameters, other.Parameters))
            {
                return true;
            }

            if (this.Parameters.Length != other.Parameters.Length)
            {
                return false;
            }

            for (int index = 0; index < this.Parameters.Length; index++)
            {
                if (!MethodSignature.typeNameComparer.Equals(
                        this.Parameters[index].ParameterType.AssemblyQualifiedName,
                        other.Parameters[index].ParameterType.AssemblyQualifiedName))
                {
                    return false;
                }
            }

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
                result ^= (uint)MethodSignature.typeNameComparer.GetHashCode(parameter.ParameterType.AssemblyQualifiedName);
            }

            return (int)result;
        }

        public override string ToString()
        {
            return this.MethodInfo.ToString();
        }

        private static bool IsCancellationToken(ParameterInfo parameter) => parameter?.ParameterType.Equals(typeof(CancellationToken)) ?? false;
    }
}
