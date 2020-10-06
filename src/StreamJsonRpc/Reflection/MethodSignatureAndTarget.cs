// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Microsoft;

    [DebuggerDisplay("{DebuggerDisplay}")]
    internal struct MethodSignatureAndTarget : IEquatable<MethodSignatureAndTarget>
    {
        public MethodSignatureAndTarget(MethodInfo method, object? target, JsonRpcMethodAttribute? attribute, SynchronizationContext? perMethodSynchronizationContext)
        {
            Requires.NotNull(method, nameof(method));

            this.Signature = new MethodSignature(method, attribute);
            this.Target = target;
            this.SynchronizationContext = perMethodSynchronizationContext;
        }

        public MethodSignature Signature { get; }

        public object? Target { get; }

        internal SynchronizationContext? SynchronizationContext { get; }

        [ExcludeFromCodeCoverage]
        private string DebuggerDisplay => $"{this.Signature} ({this.Target})";

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
            return this.Signature.GetHashCode() + (this.Target != null ? RuntimeHelpers.GetHashCode(this.Target) : 0);
        }
    }
}
