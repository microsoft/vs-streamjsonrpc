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
    using Microsoft;

    internal static class ProxyGeneration
    {
        private static readonly Type[] EmptyTypes = new Type[0];
        private static readonly AssemblyName ProxyAssemblyName = new AssemblyName(string.Format(CultureInfo.InvariantCulture, "StreamJsonRpc_Proxies_{0}", Guid.NewGuid()));
        private static readonly AssemblyBuilder AssemblyBuilder;
        private static readonly ModuleBuilder ProxyModuleBuilder;
        private static readonly ConstructorInfo ObjectCtor = typeof(object).GetTypeInfo().DeclaredConstructors.Single();
        private static readonly Dictionary<TypeInfo, TypeInfo> GeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();

        static ProxyGeneration()
        {
            AssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"rpcProxies_{Guid.NewGuid()}"), AssemblyBuilderAccess.RunAndCollect);
            ProxyModuleBuilder = AssemblyBuilder.DefineDynamicModule("rpcProxies");
        }

        internal static TypeInfo Get(TypeInfo serviceInterface)
        {
            Requires.NotNull(serviceInterface, nameof(serviceInterface));
            Requires.Argument(serviceInterface.IsInterface, nameof(serviceInterface), "Generic type argument must be an interface.");

            TypeInfo generatedType;

            lock (GeneratedProxiesByInterface)
            {
                if (GeneratedProxiesByInterface.TryGetValue(serviceInterface, out generatedType))
                {
                    return generatedType;
                }
            }

            lock (ProxyModuleBuilder)
            {
                var interfaces = new Type[]
                {
                    typeof(IDisposable),
                    serviceInterface.AsType(),
                };

                var proxyTypeBuilder = ProxyModuleBuilder.DefineType(
                    string.Format(CultureInfo.InvariantCulture, "_proxy_{0}_{1}", serviceInterface.FullName, Guid.NewGuid()),
                    TypeAttributes.Public,
                    typeof(object),
                    interfaces);

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

                foreach (var method in serviceInterface.DeclaredMethods)
                {
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
                    il.Emit(OpCodes.Ldstr, method.Name);

                    // The second argument is an array of arguments for the RPC method.
                    il.Emit(OpCodes.Ldc_I4, methodParameters.Length);
                    il.Emit(OpCodes.Newarr, typeof(object));

                    for (int i = 0; i < methodParameters.Length; i++)
                    {
                        il.Emit(OpCodes.Dup); // duplicate the array on the stack
                        il.Emit(OpCodes.Ldc_I4, i); // push the index of the array to be initialized.
                        il.Emit(OpCodes.Ldarg, i + 1); // push the associated argument
                        if (methodParameters[i].ParameterType.GetTypeInfo().IsValueType)
                        {
                            il.Emit(OpCodes.Box, methodParameters[i].ParameterType); // box if the argument is a value type
                        }

                        il.Emit(OpCodes.Stelem_Ref); // set the array element.
                    }

                    // Construct the InvokeAsync<T> method with the T argument supplied if we have a return type.
                    MethodInfo invokeAsyncMethod = method.ReturnType.GetTypeInfo().IsGenericType
                        ? invokeAsyncOfTaskOfTMethodInfo.MakeGenericMethod(method.ReturnType.GetTypeInfo().GenericTypeArguments[0])
                        : invokeAsyncOfTaskMethodInfo;
                    il.EmitCall(OpCodes.Callvirt, invokeAsyncMethod, null);
                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(methodBuilder, method);
                }

                generatedType = proxyTypeBuilder.CreateTypeInfo();
            }

            lock (GeneratedProxiesByInterface)
            {
                if (!GeneratedProxiesByInterface.TryGetValue(serviceInterface, out var raceGeneratedType))
                {
                    GeneratedProxiesByInterface.Add(serviceInterface, generatedType);
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
