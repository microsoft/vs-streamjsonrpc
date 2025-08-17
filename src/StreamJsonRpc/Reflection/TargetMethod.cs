// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Globalization;
using System.Reflection;
using StreamJsonRpc.Protocol;

namespace StreamJsonRpc;

/// <summary>
/// Represents the dispatch target of an incoming request.
/// </summary>
public sealed class TargetMethod
{
    private readonly JsonRpcRequest request;
    private readonly object? target;
    private readonly RpcTargetMetadata.TargetMethodMetadata? signature;
    private readonly object?[]? arguments;
    private SynchronizationContext? synchronizationContext;

    /// <summary>
    /// A collection of error messages. May be null until the first message is added.
    /// </summary>
    private HashSet<string>? errorMessages;

    internal TargetMethod(
        JsonRpcRequest request,
        List<MethodSignatureAndTarget> candidateMethodTargets,
        SynchronizationContext fallbackSynchronizationContext)
    {
        Requires.NotNull(request, nameof(request));
        Requires.NotNull(candidateMethodTargets, nameof(candidateMethodTargets));

        this.request = request;

        ArrayPool<object?> pool = ArrayPool<object?>.Shared;
        List<RpcArgumentDeserializationException>? argumentDeserializationExceptions = null;
        foreach (MethodSignatureAndTarget candidateMethod in candidateMethodTargets)
        {
            int parameterCount = candidateMethod.Signature.Parameters.Count;
            object?[] argumentArray = pool.Rent(parameterCount);
            try
            {
                Span<object?> args = argumentArray.AsSpan(0, parameterCount);
                if (this.TryGetArguments(request, candidateMethod.Signature, args))
                {
                    this.synchronizationContext = candidateMethod.SynchronizationContext ?? fallbackSynchronizationContext;
                    this.target = candidateMethod.Target;
                    this.signature = candidateMethod.Signature;
                    this.arguments = args.ToArray();
                    break;
                }
            }
            catch (RpcArgumentDeserializationException ex)
            {
                argumentDeserializationExceptions ??= new List<RpcArgumentDeserializationException>();
                argumentDeserializationExceptions.Add(ex);
                this.AddErrorMessage(ex.Message);
            }
            finally
            {
                pool.Return(argumentArray, clearArray: true);
            }
        }

        if (argumentDeserializationExceptions is object)
        {
            this.ArgumentDeserializationFailures = new AggregateException(argumentDeserializationExceptions);
        }
    }

    /// <summary>
    /// Gets the runtime type of the target object, if there is one.
    /// </summary>
    /// <remarks>
    /// Even when a matching target method is found, there may not be a target <em>object</em>
    /// if the target method is <see langword="static" />.
    /// </remarks>
    public Type? TargetObjectType => this.target?.GetType();

    /// <summary>
    /// Gets the <see cref="MethodInfo"/> that will be invoked to handle the request, if one was found.
    /// </summary>
    public MethodInfo? TargetMethodInfo => this.signature?.MethodInfo;

    /// <summary>
    /// Gets all the exceptions thrown while trying to deserialize arguments to candidate parameter types.
    /// </summary>
    internal AggregateException? ArgumentDeserializationFailures { get; }

    internal bool IsFound => this.signature is not null;

    internal bool AcceptsCancellationToken => this.signature?.HasCancellationTokenParameter ?? false;

    internal string LookupErrorMessage
    {
        get
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                Resources.UnableToFindMethod,
                this.request.Method,
                this.request.ArgumentCount,
                this.target?.GetType().FullName ?? "{no object}",
                string.Join("; ", this.errorMessages ?? Enumerable.Empty<string>()));
        }
    }

    internal Type? ReturnType => this.signature?.ReturnType;

    /// <inheritdoc/>
    public override string ToString()
    {
        return this.signature is not null ? $"{this.signature.MethodInfo.DeclaringType?.FullName}.{this.signature.Name}({this.GetParameterSignature()})" : "<no method>";
    }

    internal async Task<object?> InvokeAsync(CancellationToken cancellationToken)
    {
        if (this.signature is null)
        {
            throw new InvalidOperationException(this.LookupErrorMessage);
        }

        Assumes.NotNull(this.arguments);
        if (cancellationToken.CanBeCanceled && this.AcceptsCancellationToken)
        {
            this.arguments[this.arguments.Length - 1] = cancellationToken;
        }

        Assumes.NotNull(this.synchronizationContext);
        await this.synchronizationContext;
        return this.signature.MethodInfo.Invoke(!this.signature.MethodInfo.IsStatic ? this.target : null, this.arguments);
    }

    private string? GetParameterSignature() => this.signature is not null ? string.Join(", ", this.signature.Parameters.Select(p => p.ParameterType.Name)) : null;

    private void AddErrorMessage(string message)
    {
        if (this.errorMessages is null)
        {
            this.errorMessages = new HashSet<string>(StringComparer.Ordinal);
        }

        this.errorMessages.Add(message);
    }

    private bool TryGetArguments(JsonRpcRequest request, RpcTargetMetadata.TargetMethodMetadata method, Span<object?> arguments)
    {
        Requires.NotNull(request, nameof(request));
        Requires.NotNull(method, nameof(method));
        Requires.Argument(arguments.Length == method.Parameters.Count, nameof(arguments), "Length must equal number of parameters in method signature.");

        // ref and out parameters aren't supported.
        if (method.HasOutOrRefParameters)
        {
            this.AddErrorMessage(string.Format(CultureInfo.CurrentCulture, Resources.MethodHasRefOrOutParameters, method));
            return false;
        }

        // When there is a CancellationToken parameter, we require that it always be the last parameter.
        ReadOnlySpan<ParameterInfo> methodParametersExcludingCancellationToken = method.ParametersMemory.Span[..method.TotalParamCountExcludingCancellationToken];
        Span<object?> argumentsExcludingCancellationToken = arguments.Slice(0, method.TotalParamCountExcludingCancellationToken);
        if (method.HasCancellationTokenParameter)
        {
            arguments[arguments.Length - 1] = CancellationToken.None;
        }

        switch (request.TryGetTypedArguments(methodParametersExcludingCancellationToken, argumentsExcludingCancellationToken))
        {
            case JsonRpcRequest.ArgumentMatchResult.Success:
                return true;
            case JsonRpcRequest.ArgumentMatchResult.ParameterArgumentCountMismatch:
                string methodParameterCount;
                methodParameterCount = string.Format(CultureInfo.CurrentCulture, "{0} - {1}", method.RequiredParamCount, method.TotalParamCountExcludingCancellationToken);
                this.AddErrorMessage(string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.MethodParameterCountDoesNotMatch,
                    method,
                    methodParameterCount,
                    request.ArgumentCount));
                return false;
            case JsonRpcRequest.ArgumentMatchResult.ParameterArgumentTypeMismatch:
                this.AddErrorMessage(string.Format(CultureInfo.CurrentCulture, Resources.MethodParametersNotCompatible, method));
                return false;
            case JsonRpcRequest.ArgumentMatchResult.MissingArgument:
                this.AddErrorMessage(Resources.RequiredArgumentMissing);
                return false;
            default:
                return false;
        }
    }
}
