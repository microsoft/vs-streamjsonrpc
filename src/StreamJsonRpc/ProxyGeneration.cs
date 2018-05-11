// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;

    internal static class ProxyGeneration
    {
        private static readonly Type[] EmptyTypes = new Type[0];
        private static readonly AssemblyName ProxyAssemblyName = new AssemblyName(string.Format(CultureInfo.InvariantCulture, "StreamJsonRpc_Proxies_{0}", Guid.NewGuid()));
        private static readonly AssemblyBuilder AssemblyBuilder;
        private static readonly ModuleBuilder ProxyModuleBuilder;
        private static readonly ConstructorInfo ObjectCtor = typeof(object).GetTypeInfo().DeclaredConstructors.Single();
        private static readonly Dictionary<TypeInfo, TypeInfo> GeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();
        private static readonly Dictionary<TypeInfo, TypeInfo> DisposableGeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();

        static ProxyGeneration()
        {
            AssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"rpcProxies_{Guid.NewGuid()}"), AssemblyBuilderAccess.RunAndCollect);
            ProxyModuleBuilder = AssemblyBuilder.DefineDynamicModule("rpcProxies");
        }

        internal static TypeInfo Get(TypeInfo serviceInterface, bool disposable)
        {
            Requires.NotNull(serviceInterface, nameof(serviceInterface));
            Requires.Argument(serviceInterface.IsInterface, nameof(serviceInterface), "Generic type argument must be an interface.");

            TypeInfo generatedType;

            var proxyCache = disposable ? DisposableGeneratedProxiesByInterface : GeneratedProxiesByInterface;
            lock (proxyCache)
            {
                if (proxyCache.TryGetValue(serviceInterface, out generatedType))
                {
                    return generatedType;
                }
            }

            var methodNameMap = new JsonRpc.MethodNameMap(serviceInterface);

            lock (ProxyModuleBuilder)
            {
                var interfaces = new List<Type>
                {
                    serviceInterface.AsType(),
                };

                if (disposable)
                {
                    interfaces.Add(typeof(IDisposable));
                }

                var proxyTypeBuilder = ProxyModuleBuilder.DefineType(
                    string.Format(CultureInfo.InvariantCulture, "_proxy_{0}_{1}", serviceInterface.FullName, Guid.NewGuid()),
                    TypeAttributes.Public,
                    typeof(object),
                    interfaces.ToArray());

                var jsonRpcField = proxyTypeBuilder.DefineField("rpc", typeof(JsonRpc), FieldAttributes.Private | FieldAttributes.InitOnly);

                // .ctor(JsonRpc)
                {
                    var ctor = proxyTypeBuilder.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, CallingConventions.Standard, new Type[] { typeof(JsonRpc) });
                    var il = ctor.GetILGenerator();

                    // : base()
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, ObjectCtor);

                    // this.rpc = rpc;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, jsonRpcField);

                    il.Emit(OpCodes.Ret);
                }

                // IDisposable.Dispose()
                if (disposable)
                {
                    var disposeMethod = proxyTypeBuilder.DefineMethod(nameof(IDisposable.Dispose), MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual);
                    var il = disposeMethod.GetILGenerator();

                    // this.rpc.Dispose();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);
                    MethodInfo jsonRpcDisposeMethod = typeof(JsonRpc).GetTypeInfo().GetDeclaredMethods(nameof(JsonRpc.Dispose)).Single(m => m.GetParameters().Length == 0);
                    il.EmitCall(OpCodes.Callvirt, jsonRpcDisposeMethod, EmptyTypes);
                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetTypeInfo().GetDeclaredMethod(nameof(IDisposable.Dispose)));
                }

                var invokeAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeAsync) && m.GetParameters()[1].ParameterType == typeof(object[])).ToArray();
                var invokeAsyncOfTaskMethodInfo = invokeAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                var invokeAsyncOfTaskOfTMethodInfo = invokeAsyncMethodInfos.Single(m => m.IsGenericMethod);

                var invokeWithCancellationAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeWithCancellationAsync));
                var invokeWithCancellationAsyncOfTaskMethodInfo = invokeWithCancellationAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                var invokeWithCancellationAsyncOfTaskOfTMethodInfo = invokeWithCancellationAsyncMethodInfos.Single(m => m.IsGenericMethod);

                foreach (var method in serviceInterface.DeclaredMethods)
                {
                    Requires.Argument(method.ReturnType == typeof(Task) || (method.ReturnType.GetTypeInfo().IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)), nameof(serviceInterface), "Method \"{0}\" has unsupported return type \"{1}\". Only Task-returning methods are supported.", method.Name, method.ReturnType.FullName);

                    ParameterInfo[] methodParameters = method.GetParameters();
                    var methodBuilder = proxyTypeBuilder.DefineMethod(
                        method.Name,
                        MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                        method.ReturnType,
                        methodParameters.Select(p => p.ParameterType).ToArray());
                    var il = methodBuilder.GetILGenerator();

                    // this.rpc
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);

                    // First argument to InvokeAsync is the method name.
                    il.Emit(OpCodes.Ldstr, methodNameMap.GetRpcMethodName(method));

                    // The second argument is an array of arguments for the RPC method.
                    il.Emit(OpCodes.Ldc_I4, methodParameters.Count(p => p.ParameterType != typeof(CancellationToken)));
                    il.Emit(OpCodes.Newarr, typeof(object));

                    ParameterInfo cancellationTokenParameter = null;

                    for (int i = 0; i < methodParameters.Length; i++)
                    {
                        if (methodParameters[i].ParameterType == typeof(CancellationToken))
                        {
                            cancellationTokenParameter = methodParameters[i];
                            continue;
                        }

                        il.Emit(OpCodes.Dup); // duplicate the array on the stack
                        il.Emit(OpCodes.Ldc_I4, i); // push the index of the array to be initialized.
                        il.Emit(OpCodes.Ldarg, i + 1); // push the associated argument
                        if (methodParameters[i].ParameterType.GetTypeInfo().IsValueType)
                        {
                            il.Emit(OpCodes.Box, methodParameters[i].ParameterType); // box if the argument is a value type
                        }

                        il.Emit(OpCodes.Stelem_Ref); // set the array element.
                    }

                    bool hasReturnValue = method.ReturnType.GetTypeInfo().IsGenericType;
                    MethodInfo invokingMethod;
                    if (cancellationTokenParameter != null)
                    {
                        il.Emit(OpCodes.Ldarg, cancellationTokenParameter.Position + 1);
                        invokingMethod = hasReturnValue ? invokeWithCancellationAsyncOfTaskOfTMethodInfo : invokeWithCancellationAsyncOfTaskMethodInfo;
                    }
                    else
                    {
                        invokingMethod = hasReturnValue ? invokeAsyncOfTaskOfTMethodInfo : invokeAsyncOfTaskMethodInfo;
                    }

                    // Construct the InvokeAsync<T> method with the T argument supplied if we have a return type.
                    if (hasReturnValue)
                    {
                        invokingMethod = invokingMethod.MakeGenericMethod(method.ReturnType.GetTypeInfo().GenericTypeArguments[0]);
                    }

                    il.EmitCall(OpCodes.Callvirt, invokingMethod, null);
                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(methodBuilder, method);
                }

                generatedType = proxyTypeBuilder.CreateTypeInfo();
            }

            lock (proxyCache)
            {
                if (!proxyCache.TryGetValue(serviceInterface, out var raceGeneratedType))
                {
                    proxyCache.Add(serviceInterface, generatedType);
                }
                else
                {
                    // Ensure we only expose the same generated type externally.
                    generatedType = raceGeneratedType;
                }
            }

            return generatedType;
        }
    }
}
