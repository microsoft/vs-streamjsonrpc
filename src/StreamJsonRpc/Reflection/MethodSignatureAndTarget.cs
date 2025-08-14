// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace StreamJsonRpc;

[DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
internal struct MethodSignatureAndTarget : IEquatable<MethodSignatureAndTarget>
{
    internal MethodSignatureAndTarget(RpcTargetMetadata.TargetMethodMetadata signature, object? target, JsonRpcMethodAttribute? attribute, SynchronizationContext? perMethodSynchronizationContext)
    {
        this.Signature = signature;
        this.Target = target;
        this.SynchronizationContext = perMethodSynchronizationContext;
        this.Attribute = attribute ?? signature.Attribute;
    }

    internal RpcTargetMetadata.TargetMethodMetadata Signature { get; }

    internal JsonRpcMethodAttribute? Attribute { get; }

    internal object? Target { get; }

    internal SynchronizationContext? SynchronizationContext { get; }

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
}
