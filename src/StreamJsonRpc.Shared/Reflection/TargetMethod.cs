using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft;

namespace StreamJsonRpc
{
    internal sealed class TargetMethod
    {
        private readonly HashSet<string> errorMessages = new HashSet<string>(StringComparer.Ordinal);
        private readonly JsonRpcMessage request;
        private readonly object callbackObject;
        private readonly MethodInfo method;
        private readonly object[] parameters;

        internal TargetMethod(JsonRpcMessage request, object callbackObject)
        {
            Requires.NotNull(request, nameof(request));
            Requires.NotNull(callbackObject, nameof(callbackObject));

            this.request = request;
            this.callbackObject = callbackObject;

            var targetMethods = new Dictionary<MethodSignature, object[]>();
            for (TypeInfo t = callbackObject.GetType().GetTypeInfo(); t != null; t = t.BaseType?.GetTypeInfo())
            {
                foreach (MethodSignature method in t.GetDeclaredMethods(request.Method).Select(method => new MethodSignature(method)))
                {
                    if (!targetMethods.ContainsKey(method))
                    {
                        object[] parameters = TargetMethod.TryGetParameters(request, method, this.errorMessages);
                        if (parameters != null)
                        {
                            targetMethods.Add(method, parameters);
                        }
                    }
                }
            }

            if (targetMethods.Count == 1)
            {
                KeyValuePair<MethodSignature, object[]> methodWithParameters = targetMethods.First();
                this.method = methodWithParameters.Key.MethodInfo;
                this.parameters = methodWithParameters.Value;
            }
            else if (targetMethods.Count > 1)
            {
                this.method = null;
                this.parameters = null;
                this.errorMessages.Add(string.Format(CultureInfo.CurrentCulture, Resources.MoreThanOneMethodFound, string.Join("; ", targetMethods.Keys)));
            }
        }

        internal bool IsFound => this.method != null;

        internal string LookupErrorMessage
        {
            get
            {
                return  string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.UnableToFindMethod,
                    this.request.Method,
                    this.request.ParameterCount,
                    this.callbackObject.GetType().FullName,
                    string.Join("; ", this.errorMessages));
            }
        }

        internal object Invoke()
        {
            if (this.method == null)
            {
                throw new InvalidOperationException(this.LookupErrorMessage);
            }

            return this.method.Invoke(!this.method.IsStatic ? this.callbackObject : null, this.parameters);
        }

        private static object[] TryGetParameters(JsonRpcMessage request, MethodSignature method, HashSet<string> errors)
        {
            if (!method.IsPublic)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodIsNotPublic, method));
                return null;
            }

            // The method name and the number of parameters must match
            if (!string.Equals(method.Name, request.Method, StringComparison.Ordinal))
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodNameCaseIsDifferent, method, request.Method));
                return null;
            }

            if (method.HasOutOrRefParameters)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodHasRefOrOutParameters, method));
                return null;
            }

            int paramCount = request.ParameterCount;
            if (paramCount < method.RequiredParamCount || paramCount > method.TotalParamCount)
            {
                string methodParameterCount;
                if (method.RequiredParamCount == method.TotalParamCount)
                {
                    methodParameterCount = method.RequiredParamCount.ToString(CultureInfo.CurrentCulture);
                }
                else
                {
                    methodParameterCount = string.Format(CultureInfo.CurrentCulture, "{0} - {1}", method.RequiredParamCount, method.TotalParamCount);
                }

                errors.Add(string.Format(CultureInfo.CurrentCulture,
                    Resources.MethodParameterCountDoesNotMatch,
                    method,
                    methodParameterCount,
                    request.ParameterCount));

                return null;
            }

            // Parameters must be compatible
            try
            {
                return request.GetParameters(method.Parameters);
            }
            catch (Exception exception)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Resources.MethodParametersNotCompatible, method, exception.Message));
                return null;
            }
        }
    }
}