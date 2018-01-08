// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Microsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    internal sealed class TargetMethod
    {
        private readonly HashSet<string> errorMessages = new HashSet<string>(StringComparer.Ordinal);
        private readonly JsonRpcMessage request;
        private readonly object target;
        private readonly MethodInfo method;
        private readonly object[] parameters;

        internal TargetMethod(
            JsonRpcMessage request,
            JsonSerializer jsonSerializer,
            IEnumerable<MethodSignatureAndTarget> candidateMethodTargets)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));
            Requires.NotNull(candidateMethodTargets, nameof(candidateMethodTargets));

            this.request = request;

            var targetMethods = new Dictionary<MethodSignatureAndTarget, object[]>();
            foreach (var method in candidateMethodTargets)
            {
                this.TryAddMethod(request, targetMethods, method, jsonSerializer);
            }

            KeyValuePair<MethodSignatureAndTarget, object[]> methodWithParameters = targetMethods.FirstOrDefault();
            if (methodWithParameters.Key.Signature != null)
            {
                this.target = methodWithParameters.Key.Target;
                this.method = methodWithParameters.Key.Signature.MethodInfo;
                this.parameters = methodWithParameters.Value;
                this.AcceptsCancellationToken = methodWithParameters.Key.Signature.HasCancellationTokenParameter;
            }
        }

        internal bool IsFound => this.method != null;

        internal bool AcceptsCancellationToken { get; private set; }

        internal string LookupErrorMessage
        {
            get
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.UnableToFindMethod,
                    this.request.Method,
                    this.request.ParameterCount,
                    this.target?.GetType().FullName ?? "{no object}",
                    string.Join("; ", this.errorMessages));
            }
        }

        internal object Invoke(CancellationToken cancellationToken)
        {
            if (this.method == null)
            {
                throw new InvalidOperationException(this.LookupErrorMessage);
            }

            if (cancellationToken.CanBeCanceled && this.AcceptsCancellationToken)
            {
                for (int i = this.parameters.Length - 1; i >= 0; i--)
                {
                    if (this.parameters[i] is CancellationToken)
                    {
                        this.parameters[i] = cancellationToken;
                        break;
                    }
                }
            }

            return this.method.Invoke(!this.method.IsStatic ? this.target : null, this.parameters);
        }

        private static object[] TryGetParameters(JsonRpcMessage request, MethodSignature method, HashSet<string> errors, JsonSerializer jsonSerializer, string requestMethodName)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(method, nameof(method));
            Requires.NotNull(errors, nameof(errors));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));
            Requires.NotNullOrEmpty(requestMethodName, nameof(requestMethodName));

            // ref and out parameters aren't supported.
            if (method.HasOutOrRefParameters)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodHasRefOrOutParameters, method));
                return null;
            }

            if (request.Parameters != null && request.Parameters.Type == JTokenType.Object)
            {
                // If the parameter passed is an object, then we want to find the matching method with the same name and the method only takes a JToken as a parameter,
                // and possibly a CancellationToken
                if (method.Parameters.Length < 1 || method.Parameters[0].ParameterType != typeof(JToken))
                {
                    return null;
                }

                var args = new List<object>(2);
                args.Add(request.Parameters);

                if (method.Parameters.Length > 1 && method.Parameters[1].ParameterType == typeof(CancellationToken))
                {
                    args.Add(CancellationToken.None);
                }

                if (method.Parameters.Length > 2)
                {
                    // We don't support methods with more than two parameters.
                    return null;
                }

                return args.ToArray();
            }

            // The number of parameters must fall within required and total parameters.
            int paramCount = request.ParameterCount;
            if (paramCount < method.RequiredParamCount || paramCount > method.TotalParamCountExcludingCancellationToken)
            {
                string methodParameterCount;
                if (method.RequiredParamCount == method.TotalParamCountExcludingCancellationToken)
                {
                    methodParameterCount = method.RequiredParamCount.ToString(CultureInfo.CurrentCulture);
                }
                else
                {
                    methodParameterCount = string.Format(CultureInfo.CurrentCulture, "{0} - {1}", method.RequiredParamCount, method.TotalParamCountExcludingCancellationToken);
                }

                errors.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.MethodParameterCountDoesNotMatch,
                    method,
                    methodParameterCount,
                    request.ParameterCount));

                return null;
            }

            // Parameters must be compatible
            try
            {
                return request.GetParameters(method.Parameters, jsonSerializer);
            }
            catch (Exception exception)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodParametersNotCompatible, method, exception.Message));
                return null;
            }
        }

        private bool TryAddMethod(JsonRpcMessage request, Dictionary<MethodSignatureAndTarget, object[]> targetMethods, MethodSignatureAndTarget method, JsonSerializer jsonSerializer)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(targetMethods, nameof(targetMethods));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));

            object[] parameters = TryGetParameters(request, method.Signature, this.errorMessages, jsonSerializer, request.Method);
            if (parameters != null)
            {
                targetMethods.Add(method, parameters);
                return true;
            }

            return false;
        }
    }
}