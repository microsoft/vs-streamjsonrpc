// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Reflection;
    using Microsoft;

    internal struct MethodSignatureAndTarget : IEquatable<MethodSignatureAndTarget>
    {
        public MethodSignatureAndTarget(MethodInfo method, object target)
        {
            Requires.NotNull(method, nameof(method));

            this.Signature = new MethodSignature(method);
            this.Target = target;
        }

        public MethodSignature Signature { get; }

        public object Target { get; }

        public override bool Equals(object obj)
        {
            return obj is MethodSignatureAndTarget other
                && this.Equals(other);
        }

        public bool Equals(MethodSignatureAndTarget other)
        {
            return this.Signature.Equals(other.Signature)
                && object.Equals(this.Target, other.Target);
        }

        public override int GetHashCode()
        {
            return this.Signature.GetHashCode() + (this.Target?.GetHashCode() ?? 0);
        }
    }
}
