// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StreamJsonRpc;

[DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
internal class MethodSignatureAndTarget : IEquatable<MethodSignatureAndTarget>
{
    private const string RequiredAttributeFullName = "System.ComponentModel.DataAnnotations.RequiredAttribute";
    private readonly Func<string, string>? parameterNameTransform;
    private readonly ParameterInfo[]? effectiveParameters;
    private readonly ParameterBindingInfo[]? parameterBindingInfo;

    /// <summary>
    /// The list of RPC parameter names, or an empty list if we're using the ordinary CLR parameter names.
    /// </summary>
    private ReadOnlyMemory<string?>? parameterNamesExcludingCancellationToken;

    internal MethodSignatureAndTarget(
        RpcTargetMetadata.TargetMethodMetadata signature,
        object? target,
        JsonRpcMethodAttribute? attribute,
        SynchronizationContext? perMethodSynchronizationContext,
        Func<string, string>? parameterNameTransform = null,
        bool allowFlexibleNamedArgumentMatching = false)
    {
        this.Signature = signature;
        this.Target = target;
        this.SynchronizationContext = perMethodSynchronizationContext;
        this.parameterNameTransform = parameterNameTransform;
        this.Attribute = attribute ?? signature.Attribute;
        this.AllowFlexibleNamedArgumentMatching = allowFlexibleNamedArgumentMatching;
        if (allowFlexibleNamedArgumentMatching)
        {
            (this.parameterBindingInfo, this.HasConflictingInterfaceDefaultValues) = CreateParameterBindingInfo(signature, target);
            this.effectiveParameters = new ParameterInfo[this.parameterBindingInfo.Length];
            for (int i = 0; i < this.effectiveParameters.Length; i++)
            {
                ParameterBindingInfo bindingInfo = this.parameterBindingInfo[i];
                this.effectiveParameters[i] = new EffectiveParameterInfo(signature.Parameters[i], bindingInfo.IsRequired, bindingInfo.HasDefaultValue, bindingInfo.DefaultValue);
            }
        }
    }

    internal RpcTargetMetadata.TargetMethodMetadata Signature { get; }

    internal JsonRpcMethodAttribute? Attribute { get; }

    internal object? Target { get; }

    internal SynchronizationContext? SynchronizationContext { get; }

    internal bool AllowFlexibleNamedArgumentMatching { get; }

    internal bool HasConflictingInterfaceDefaultValues { get; }

    internal ReadOnlySpan<string?> ParameterNamesExcludingCancellationToken => (this.parameterNamesExcludingCancellationToken ??= GetEffectiveParameterNames(this.Signature, this.parameterNameTransform)).Span;

    internal ReadOnlySpan<ParameterBindingInfo> ParameterBindings => this.parameterBindingInfo;

    internal ReadOnlyMemory<ParameterInfo> EffectiveParameters => this.effectiveParameters;

    [ExcludeFromCodeCoverage]
    private string DebuggerDisplay => this.ToString();

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is MethodSignatureAndTarget other
            && this.Equals(other);
    }

    /// <inheritdoc/>
    public bool Equals(MethodSignatureAndTarget? other)
    {
        return other is not null && this.Signature.Equals(other.Signature) && object.ReferenceEquals(this.Target, other.Target);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return this.Signature.GetHashCode() + (this.Target is not null ? RuntimeHelpers.GetHashCode(this.Target) : 0);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{this.Signature} ({this.Target})";

    private static (ParameterBindingInfo[] ParameterBindingInfo, bool HasConflictingInterfaceDefaultValues) CreateParameterBindingInfo(RpcTargetMetadata.TargetMethodMetadata signature, object? target)
    {
        IReadOnlyList<ParameterInfo> contractParameters = signature.Parameters;
        MethodInfo implementationMethod = GetImplementationMethod(signature.MethodInfo, target) ?? signature.MethodInfo;
        ParameterInfo[] implementationParameters = implementationMethod.GetParameters();
        IReadOnlyList<MethodInfo> interfaceMethods = GetApplicableInterfaceMethods(signature.MethodInfo, implementationMethod, target);
        var result = new ParameterBindingInfo[contractParameters.Count];
        bool hasConflictingInterfaceDefaultValues = false;

        for (int parameterIndex = 0; parameterIndex < result.Length; parameterIndex++)
        {
            ParameterInfo contractParameter = contractParameters[parameterIndex];
            ParameterInfo implementationParameter = implementationParameters.Length > parameterIndex ? implementationParameters[parameterIndex] : contractParameter;
            bool isRequired = HasRequiredAttribute(contractParameter) || HasRequiredAttribute(implementationParameter);
            bool hasInterfaceDefaultValue = false;
            bool hasConflictingDefaultValue = false;
            object? interfaceDefaultValue = null;

            foreach (MethodInfo interfaceMethod in interfaceMethods)
            {
                ParameterInfo[] interfaceParameters = interfaceMethod.GetParameters();
                if (interfaceParameters.Length <= parameterIndex)
                {
                    continue;
                }

                ParameterInfo interfaceParameter = interfaceParameters[parameterIndex];
                isRequired |= HasRequiredAttribute(interfaceParameter);
                if (!interfaceParameter.HasDefaultValue)
                {
                    continue;
                }

                if (!hasInterfaceDefaultValue)
                {
                    hasInterfaceDefaultValue = true;
                    interfaceDefaultValue = interfaceParameter.DefaultValue;
                }
                else if (!Equals(interfaceDefaultValue, interfaceParameter.DefaultValue))
                {
                    hasConflictingDefaultValue = true;
                }
            }

            if (hasConflictingDefaultValue)
            {
                hasConflictingInterfaceDefaultValues = true;
                hasInterfaceDefaultValue = false;
            }

            result[parameterIndex] = hasInterfaceDefaultValue
                ? new ParameterBindingInfo(isRequired, HasDefaultValue: true, interfaceDefaultValue)
                : new ParameterBindingInfo(isRequired, implementationParameter.HasDefaultValue, implementationParameter.DefaultValue);
        }

        return (result, hasConflictingInterfaceDefaultValues);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The interface and target method metadata is already required in order to register and invoke the RPC method.")]
    private static MethodInfo? GetImplementationMethod(MethodInfo method, object? target)
    {
        if (target is null || method.DeclaringType?.IsInterface is not true)
        {
            return method;
        }

        InterfaceMapping interfaceMapping = target.GetType().GetInterfaceMap(method.DeclaringType);
        int methodIndex = Array.IndexOf(interfaceMapping.InterfaceMethods, method);
        return methodIndex >= 0 ? interfaceMapping.TargetMethods[methodIndex] : null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The interface and target method metadata is already required in order to register and invoke the RPC method.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The target type's implemented interfaces are already required when registering its RPC methods.")]
    private static IReadOnlyList<MethodInfo> GetApplicableInterfaceMethods(MethodInfo contractMethod, MethodInfo implementationMethod, object? target)
    {
        if (contractMethod.DeclaringType?.IsInterface is true)
        {
            return [contractMethod];
        }

        if (target is null || implementationMethod.IsStatic)
        {
            return [];
        }

        List<MethodInfo>? result = null;
        foreach (Type interfaceType in target.GetType().GetInterfaces())
        {
            InterfaceMapping interfaceMapping = target.GetType().GetInterfaceMap(interfaceType);
            for (int i = 0; i < interfaceMapping.TargetMethods.Length; i++)
            {
                if (interfaceMapping.TargetMethods[i] == implementationMethod)
                {
                    (result ??= []).Add(interfaceMapping.InterfaceMethods[i]);
                }
            }
        }

        return result ?? (IReadOnlyList<MethodInfo>)[];
    }

    private static bool HasRequiredAttribute(ParameterInfo parameter)
    {
        foreach (CustomAttributeData attribute in parameter.GetCustomAttributesData())
        {
            for (Type? attributeType = attribute.AttributeType; attributeType is not null; attributeType = attributeType.BaseType)
            {
                if (attributeType.FullName == RequiredAttributeFullName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the RPC parameter names for a method, excluding any <see cref="CancellationToken"/> parameter.
    /// </summary>
    /// <param name="signature">The method signature.</param>
    /// <param name="parameterNameTransform">A runtime-supplied transform for parameter names.</param>
    /// <returns>The list of RPC parameter names, or an empty list if we're using the ordinary CLR parameter names.</returns>
    private static ReadOnlyMemory<string?> GetEffectiveParameterNames(RpcTargetMetadata.TargetMethodMetadata signature, Func<string, string>? parameterNameTransform)
    {
        int parameterCount = signature.TotalParamCountExcludingCancellationToken;
        if (parameterCount == 0 || (parameterNameTransform is null && !signature.HasRenamedParameters))
        {
            return ReadOnlyMemory<string?>.Empty;
        }

        string?[]? result = null;
        for (int i = 0; i < parameterCount; i++)
        {
            ParameterInfo parameter = signature.Parameters[i];
            string? parameterName = signature.ParameterNames[i];
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

    internal readonly record struct ParameterBindingInfo(bool IsRequired, bool HasDefaultValue, object? DefaultValue);

    internal sealed class EffectiveParameterInfo : ParameterInfo
    {
        private readonly ParameterInfo inner;
        private readonly bool hasDefaultValue;
        private readonly object? defaultValue;

        internal EffectiveParameterInfo(ParameterInfo inner, bool isRequired, bool hasDefaultValue, object? defaultValue)
        {
            this.inner = inner;
            this.IsRequired = isRequired;
            this.hasDefaultValue = hasDefaultValue;
            this.defaultValue = defaultValue;
        }

        public override ParameterAttributes Attributes => this.inner.Attributes;

        public override object? DefaultValue => this.defaultValue;

        public override bool HasDefaultValue => this.hasDefaultValue;

        public override MemberInfo Member => this.inner.Member;

        public override string? Name => this.inner.Name;

        public override Type ParameterType => this.inner.ParameterType;

        public override int Position => this.inner.Position;

        public override object? RawDefaultValue => this.defaultValue;

        internal bool IsRequired { get; }

        public override object[] GetCustomAttributes(bool inherit) => this.inner.GetCustomAttributes(inherit);

        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => this.inner.GetCustomAttributes(attributeType, inherit);

        public override IList<CustomAttributeData> GetCustomAttributesData() => this.inner.GetCustomAttributesData();

        public override bool IsDefined(Type attributeType, bool inherit) => this.inner.IsDefined(attributeType, inherit);
    }
}
