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
            object target,
            JsonSerializer jsonSerializer,
            IReadOnlyDictionary<string, string> requestMethodToClrMethodMap)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(target, nameof(target));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));
            Requires.NotNull(requestMethodToClrMethodMap, nameof(requestMethodToClrMethodMap));

            this.request = request;
            this.target = target;

            if (requestMethodToClrMethodMap.TryGetValue(request.Method, out string clrMethodName))
            {
                var targetMethods = new Dictionary<MethodSignature, object[]>();
                for (TypeInfo t = target.GetType().GetTypeInfo(); t != null; t = t.BaseType?.GetTypeInfo())
                {
                    foreach (MethodInfo method in t.GetDeclaredMethods(clrMethodName))
                    {
                        this.TryAddMethod(request, targetMethods, method, jsonSerializer, request.Method);
                    }
                }

                if (targetMethods.Count == 1)
                {
                    KeyValuePair<MethodSignature, object[]> methodWithParameters = targetMethods.First();
                    this.method = methodWithParameters.Key.MethodInfo;
                    this.parameters = methodWithParameters.Value;
                    this.AcceptsCancellationToken = methodWithParameters.Key.HasCancellationTokenParameter;
                }
                else if (targetMethods.Count > 1)
                {
                    this.method = null;
                    this.parameters = null;
                    this.errorMessages.Add(string.Format(CultureInfo.CurrentCulture, Resources.MoreThanOneMethodFound, string.Join("; ", targetMethods.Keys)));
                }
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
                    this.target.GetType().FullName,
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

            if (!method.IsPublic)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodIsNotPublic, method));
                return null;
            }

            // ref and out parameters aren't supported.
            if (method.HasOutOrRefParameters)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodHasRefOrOutParameters, method));
                return null;
            }

            if (request.Parameters != null && request.Parameters.Type == JTokenType.Object)
            {
                // If the parameter passed is an object, then we want to find the matching method with the same name and the method only takes a JToken as a parameter.
                if (method.Parameters.Length != 1)
                {
                    return null;
                }

                if (method.Parameters[0].ParameterType != typeof(JToken))
                {
                    return null;
                }

                return new object[] { request.Parameters };
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

        private bool TryAddMethod(JsonRpcMessage request, Dictionary<MethodSignature, object[]> targetMethods, MethodInfo method, JsonSerializer jsonSerializer, string requestMethodName)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(targetMethods, nameof(targetMethods));
            Requires.NotNull(method, nameof(method));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));
            Requires.NotNullOrEmpty(requestMethodName, nameof(requestMethodName));

            var methodSignature = new MethodSignature(method);
            if (!targetMethods.ContainsKey(methodSignature))
            {
                object[] parameters = TryGetParameters(request, methodSignature, this.errorMessages, jsonSerializer, requestMethodName);
                if (parameters != null)
                {
                    targetMethods.Add(methodSignature, parameters);
                    return true;
                }
            }

            return false;
        }
    }
}