// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StreamJsonRpc;

[DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
internal struct MethodSignatureAndTarget : IEquatable<MethodSignatureAndTarget>
{
    private readonly ReadOnlyMemory<string?> parameterNamesExcludingCancellationToken;

    internal MethodSignatureAndTarget(RpcTargetMetadata.TargetMethodMetadata signature, object? target, JsonRpcMethodAttribute? attribute, SynchronizationContext? perMethodSynchronizationContext, Func<string, string>? parameterNameTransform = null)
    {
        this.Signature = signature;
        this.Target = target;
        this.SynchronizationContext = perMethodSynchronizationContext;
        this.Attribute = attribute ?? signature.Attribute;
        this.parameterNamesExcludingCancellationToken = GetEffectiveParameterNames(signature, parameterNameTransform);
    }

    internal RpcTargetMetadata.TargetMethodMetadata Signature { get; }

    internal JsonRpcMethodAttribute? Attribute { get; }

    internal object? Target { get; }

    internal SynchronizationContext? SynchronizationContext { get; }

    internal ReadOnlySpan<string?> ParameterNamesExcludingCancellationToken => this.parameterNamesExcludingCancellationToken.Span;

    [ExcludeFromCodeCoverage]
    private string DebuggerDisplay => this.ToString();

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is MethodSignatureAndTarget other
            && this.Equals(other);
    }

    /// <inheritdoc/>
    public bool Equals(MethodSignatureAndTarget other)
    {
        return this.Signature.Equals(other.Signature)
            && object.ReferenceEquals(this.Target, other.Target);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return this.Signature.GetHashCode() + (this.Target is not null ? RuntimeHelpers.GetHashCode(this.Target) : 0);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{this.Signature} ({this.Target})";

    private static ReadOnlyMemory<string?> GetEffectiveParameterNames(RpcTargetMetadata.TargetMethodMetadata signature, Func<string, string>? parameterNameTransform)
    {
        int parameterCount = signature.TotalParamCountExcludingCancellationToken;
        if (parameterCount == 0)
        {
            return ReadOnlyMemory<string?>.Empty;
        }

        string?[]? result = null;
        for (int i = 0; i < parameterCount; i++)
        {
            ParameterInfo parameter = signature.Parameters[i];
            string? parameterName = parameter.GetCustomAttribute<JsonRpcParameterAttribute>()?.Name ?? parameter.Name;
            if (parameterNameTransform is not null && parameterName is not null)
            {
                parameterName = parameterNameTransform(parameterName);
                Requires.Argument(parameterName is not null, nameof(parameterNameTransform), "Delegate returned a null parameter name.");
            }

            if (!StringComparer.Ordinal.Equals(parameterName, parameter.Name))
            {
                if (result is null)
                {
                    // Lazily allocate and back-fill with the original (unchanged) names for all preceding parameters.
                    result = new string?[parameterCount];
                    for (int j = 0; j < i; j++)
                    {
                        result[j] = signature.Parameters[j].Name;
                    }
                }

                result[i] = parameterName;
            }
            else if (result is not null)
            {
                result[i] = parameterName;
            }
        }

        return result is not null ? result : ReadOnlyMemory<string?>.Empty;
    }
}
