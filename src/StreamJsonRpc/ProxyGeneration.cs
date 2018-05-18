// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
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
        private static readonly MethodInfo DelegateCombineMethod = typeof(Delegate).GetRuntimeMethod(nameof(Delegate.Combine), new Type[] { typeof(Delegate), typeof(Delegate) });
        private static readonly MethodInfo DelegateRemoveMethod = typeof(Delegate).GetRuntimeMethod(nameof(Delegate.Remove), new Type[] { typeof(Delegate), typeof(Delegate) });
        private static readonly AssemblyBuilder AssemblyBuilder;
        private static readonly ModuleBuilder ProxyModuleBuilder;
        private static readonly ConstructorInfo ObjectCtor = typeof(object).GetTypeInfo().DeclaredConstructors.Single();
        private static readonly Dictionary<TypeInfo, TypeInfo> GeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();
        private static readonly Dictionary<TypeInfo, TypeInfo> DisposableGeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();
        private static readonly MethodInfo CompareExchangeMethod = (from method in typeof(Interlocked).GetRuntimeMethods()
                                                                    where method.Name == nameof(Interlocked.CompareExchange)
                                                                    let parameters = method.GetParameters()
                                                                    where parameters.Length == 3 && parameters.All(p => p.ParameterType.IsGenericParameter || p.ParameterType.GetTypeInfo().ContainsGenericParameters)
                                                                    select method).Single();

        static ProxyGeneration()
        {
            AssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"rpcProxies_{Guid.NewGuid()}"), AssemblyBuilderAccess.RunAndCollect);
            ProxyModuleBuilder = AssemblyBuilder.DefineDynamicModule("rpcProxies");
        }

        internal static TypeInfo Get(TypeInfo serviceInterface, bool disposable)
        {
            Requires.NotNull(serviceInterface, nameof(serviceInterface));
            VerifySupported(serviceInterface.IsInterface, Resources.ClientProxyTypeArgumentMustBeAnInterface, serviceInterface);

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

                VerifySupported(!serviceInterface.DeclaredProperties.Any(), Resources.UnsupportedPropertiesOnClientProxyInterface, serviceInterface);

                // Implement events
                var ctorActions = new List<Action<ILGenerator>>();
                foreach (var evt in serviceInterface.DeclaredEvents)
                {
                    VerifySupported(evt.EventHandlerType.Equals(typeof(EventHandler)) || (evt.EventHandlerType.GetTypeInfo().IsGenericType && evt.EventHandlerType.GetGenericTypeDefinition().Equals(typeof(EventHandler<>))), Resources.UnsupportedEventHandlerTypeOnClientProxyInterface, evt);

                    // public event EventHandler EventName;
                    var evtBuilder = proxyTypeBuilder.DefineEvent(evt.Name, evt.Attributes, evt.EventHandlerType);

                    // private EventHandler eventName;
                    var evtField = proxyTypeBuilder.DefineField(evt.Name, evt.EventHandlerType, FieldAttributes.Private);

                    // add_EventName
                    var addRemoveHandlerParams = new Type[] { evt.EventHandlerType };
                    const MethodAttributes methodAttributes = MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual;
                    var addMethod = proxyTypeBuilder.DefineMethod($"add_{evt.Name}", methodAttributes, null, addRemoveHandlerParams);
                    ImplementEventAccessor(addMethod.GetILGenerator(), evtField, DelegateCombineMethod);
                    evtBuilder.SetAddOnMethod(addMethod);

                    // remove_EventName
                    var removeMethod = proxyTypeBuilder.DefineMethod($"remove_{evt.Name}", methodAttributes, null, addRemoveHandlerParams);
                    ImplementEventAccessor(removeMethod.GetILGenerator(), evtField, DelegateRemoveMethod);
                    evtBuilder.SetRemoveOnMethod(removeMethod);

                    // void OnEventName(EventArgs args)
                    var eventArgsType = evt.EventHandlerType.GetTypeInfo().GetDeclaredMethod(nameof(EventHandler.Invoke)).GetParameters()[1].ParameterType;
                    var raiseEventMethod = proxyTypeBuilder.DefineMethod(
                        $"On{evt.Name}",
                        MethodAttributes.HideBySig | MethodAttributes.Private,
                        null,
                        new Type[] { eventArgsType });
                    ImplementRaiseEventMethod(raiseEventMethod.GetILGenerator(), evtField, jsonRpcField);

                    ctorActions.Add(new Action<ILGenerator>(il =>
                    {
                        var addLocalRpcMethod = typeof(JsonRpc).GetRuntimeMethod(nameof(JsonRpc.AddLocalRpcMethod), new Type[] { typeof(string), typeof(Delegate) });
                        var delegateCtor = typeof(Action<>).MakeGenericType(eventArgsType).GetTypeInfo().DeclaredConstructors.Single();

                        // rpc.AddLocalRpcMethod("EventName", new Action<EventArgs>(this.OnEventName));
                        il.Emit(OpCodes.Ldarg_1); // .ctor's rpc parameter
                        il.Emit(OpCodes.Ldstr, evt.Name);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldftn, raiseEventMethod);
                        il.Emit(OpCodes.Newobj, delegateCtor);
                        il.Emit(OpCodes.Callvirt, addLocalRpcMethod);
                    }));
                }

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

                    // Emit IL that supports events.
                    foreach (var action in ctorActions)
                    {
                        action(il);
                    }

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

                foreach (var method in serviceInterface.DeclaredMethods.Where(m => !m.IsSpecialName))
                {
                    VerifySupported(method.ReturnType == typeof(Task) || (method.ReturnType.GetTypeInfo().IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)), Resources.UnsupportedMethodReturnTypeOnClientProxyInterface, method, method.ReturnType.FullName);
                    VerifySupported(!method.IsGenericMethod, Resources.UnsupportedGenericMethodsOnClientProxyInterface, method);

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

        private static void ImplementRaiseEventMethod(ILGenerator il, FieldBuilder evtField, FieldBuilder jsonRpcField)
        {
            var retLabel = il.DefineLabel();
            var invokeLabel = il.DefineLabel();

            // var eventName = this.EventName;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, evtField);

            // if (eventName != null) goto Invoke;
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue_S, invokeLabel);

            // Condition was false, so clear the stack and return.
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Br_S, retLabel);

            // Invoke:
            il.MarkLabel(invokeLabel);

            // eventName.Invoke(this.rpc, args);
            // we already have eventName as a pseudo-local on our stack.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, jsonRpcField);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, evtField.FieldType.GetTypeInfo().GetDeclaredMethod(nameof(EventHandler.Invoke)));

            il.MarkLabel(retLabel);
            il.Emit(OpCodes.Ret);
        }

        private static void ImplementEventAccessor(ILGenerator il, FieldInfo evtField, MethodInfo combineOrRemoveMethod)
        {
            var loopStart = il.DefineLabel();

            il.DeclareLocal(evtField.FieldType); // loc_0
            il.DeclareLocal(evtField.FieldType); // loc_1
            il.DeclareLocal(evtField.FieldType); // loc_2

            // EventHandler eventHandler = this.EventName;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, evtField);
            il.Emit(OpCodes.Stloc_0);

            il.MarkLabel(loopStart);
            {
                // var eventHandler2 = eventHandler;
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Stloc_1);

                // EventHandler value2 = (EventHandler)Delegate.CombineOrRemove(eventHandler2, value);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Ldarg_1);
                il.EmitCall(OpCodes.Call, combineOrRemoveMethod, EmptyTypes);
                il.Emit(OpCodes.Castclass, evtField.FieldType);
                il.Emit(OpCodes.Stloc_2);

                // eventHandler = Interlocked.CompareExchange<EventHandler>(ref this.SomethingChanged, value2, eventHandler2);
                var compareExchangeClosedGeneric = CompareExchangeMethod.MakeGenericMethod(evtField.FieldType);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, evtField);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Ldloc_1);
                il.EmitCall(OpCodes.Call, compareExchangeClosedGeneric, EmptyTypes);
                il.Emit(OpCodes.Stloc_0);

                // while ((object)eventHandler != eventHandler2);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Bne_Un_S, loopStart);
            }

            il.Emit(OpCodes.Ret);
        }

        private static void VerifySupported(bool condition, string messageFormat, MemberInfo problematicMember, params object[] otherArgs)
        {
            if (!condition)
            {
                object[] formattingArgs = new object[1 + otherArgs?.Length ?? 0];
                formattingArgs[0] = problematicMember.DeclaringType.FullName + "." + problematicMember.Name;
                if (otherArgs?.Length > 0)
                {
                    Array.Copy(otherArgs, 0, formattingArgs, 1, otherArgs.Length);
                }

                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, messageFormat, formattingArgs));
            }
        }
    }
}
