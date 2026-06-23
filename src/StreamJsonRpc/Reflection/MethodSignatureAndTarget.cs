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

        var result = new string?[parameterCount];
        bool customNameDetected = false;
        for (int i = 0; i < parameterCount; i++)
        {
            ParameterInfo parameter = signature.Parameters[i];
            string? parameterName = parameter.GetCustomAttribute<JsonRpcParameterAttribute>()?.Name ?? parameter.Name;
            if (!StringComparer.Ordinal.Equals(parameterName, parameter.Name))
            {
                customNameDetected = true;
            }

            if (parameterName is not null && parameterNameTransform is not null)
            {
                parameterName = parameterNameTransform(parameterName);
                Requires.Argument(parameterName is not null, nameof(parameterNameTransform), "Delegate returned a null parameter name.");
                if (!StringComparer.Ordinal.Equals(parameterName, parameter.Name))
                {
                    customNameDetected = true;
                }
            }

            result[i] = parameterName;
        }

        return customNameDetected ? result : ReadOnlyMemory<string?>.Empty;
    }
}
