// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Uncomment the SaveAssembly symbol and run one test to save the generated DLL for inspection in ILSpy as part of debugging.
#if NETFRAMEWORK
////#define SaveAssembly
#endif

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using CodeGenHelpers = StreamJsonRpc.Reflection.CodeGenHelpers;

    internal static class ProxyGeneration
    {
        private static readonly List<(ImmutableHashSet<AssemblyName> SkipVisibilitySet, ModuleBuilder Builder)> TransparentProxyModuleBuilderByVisibilityCheck = new List<(ImmutableHashSet<AssemblyName>, ModuleBuilder)>();
        private static readonly object BuilderLock = new object();

        private static readonly Type[] EmptyTypes = new Type[0];
        private static readonly AssemblyName ProxyAssemblyName = new AssemblyName(string.Format(CultureInfo.InvariantCulture, "StreamJsonRpc_Proxies_{0}", Guid.NewGuid()));
        private static readonly MethodInfo DelegateCombineMethod = typeof(Delegate).GetRuntimeMethod(nameof(Delegate.Combine), new Type[] { typeof(Delegate), typeof(Delegate) })!;
        private static readonly MethodInfo DelegateRemoveMethod = typeof(Delegate).GetRuntimeMethod(nameof(Delegate.Remove), new Type[] { typeof(Delegate), typeof(Delegate) })!;
        private static readonly MethodInfo CancellationTokenNonePropertyGetter = typeof(CancellationToken).GetRuntimeProperty(nameof(CancellationToken.None))!.GetMethod!;
        private static readonly ConstructorInfo ObjectCtor = typeof(object).GetTypeInfo().DeclaredConstructors.Single();
        private static readonly Dictionary<TypeInfo, TypeInfo> GeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();
        private static readonly MethodInfo CompareExchangeMethod = (from method in typeof(Interlocked).GetRuntimeMethods()
                                                                    where method.Name == nameof(Interlocked.CompareExchange)
                                                                    let parameters = method.GetParameters()
                                                                    where parameters.Length == 3 && parameters.All(p => p.ParameterType.IsGenericParameter || p.ParameterType.GetTypeInfo().ContainsGenericParameters)
                                                                    select method).Single();

        private static readonly MethodInfo MethodNameTransformPropertyGetter = typeof(JsonRpcProxyOptions).GetRuntimeProperty(nameof(JsonRpcProxyOptions.MethodNameTransform))!.GetMethod!;
        private static readonly MethodInfo MethodNameTransformInvoke = typeof(Func<string, string>).GetRuntimeMethod(nameof(JsonRpcProxyOptions.MethodNameTransform.Invoke), new Type[] { typeof(string) })!;
        private static readonly MethodInfo EventNameTransformPropertyGetter = typeof(JsonRpcProxyOptions).GetRuntimeProperty(nameof(JsonRpcProxyOptions.EventNameTransform))!.GetMethod!;
        private static readonly MethodInfo EventNameTransformInvoke = typeof(Func<string, string>).GetRuntimeMethod(nameof(JsonRpcProxyOptions.EventNameTransform.Invoke), new Type[] { typeof(string) })!;
        private static readonly MethodInfo ServerRequiresNamedArgumentsPropertyGetter = typeof(JsonRpcProxyOptions).GetRuntimeProperty(nameof(JsonRpcProxyOptions.ServerRequiresNamedArguments))!.GetMethod!;
        private static readonly MethodInfo ObjectFinalizeMethod = typeof(object).GetTypeInfo().GetDeclaredMethod("Finalize");
        private static readonly MethodInfo NotifyAbandonedProxyMethod = typeof(JsonRpc).GetTypeInfo().GetDeclaredMethod(nameof(JsonRpc.NotifyAbandonedProxy));
        private static readonly MethodInfo GetCurrentDomainMethod = typeof(AppDomain).GetTypeInfo().GetDeclaredProperty(nameof(AppDomain.CurrentDomain)).GetMethod;
        private static readonly MethodInfo IsFinalizingForUnloadMethod = typeof(AppDomain).GetTypeInfo().GetDeclaredMethod(nameof(AppDomain.IsFinalizingForUnload));
        private static readonly MethodInfo GetTypeFromHandleMethod = typeof(Type).GetTypeInfo().GetDeclaredMethod(nameof(Type.GetTypeFromHandle));

        /// <summary>
        /// Gets a dynamically generated type that implements a given interface in terms of a <see cref="JsonRpc"/> instance.
        /// </summary>
        /// <param name="serviceInterface">The interface that describes the RPC contract, and that the client proxy should implement.</param>
        /// <returns>The generated type.</returns>
        internal static TypeInfo Get(TypeInfo serviceInterface)
        {
            Requires.NotNull(serviceInterface, nameof(serviceInterface));
            VerifySupported(serviceInterface.IsInterface, Resources.ClientProxyTypeArgumentMustBeAnInterface, serviceInterface);

            TypeInfo? generatedType;

            lock (BuilderLock)
            {
                if (GeneratedProxiesByInterface.TryGetValue(serviceInterface, out generatedType))
                {
                    return generatedType;
                }

                ModuleBuilder proxyModuleBuilder = GetProxyModuleBuilder(serviceInterface);

                var methodNameMap = new JsonRpc.MethodNameMap(serviceInterface);

                var interfaces = new List<Type>
                {
                    serviceInterface.AsType(),
                };

                interfaces.Add(typeof(IJsonRpcClientProxy));

                TypeBuilder proxyTypeBuilder = proxyModuleBuilder.DefineType(
                    string.Format(CultureInfo.InvariantCulture, "_proxy_{0}_{1}", serviceInterface.FullName, Guid.NewGuid()),
                    TypeAttributes.Public,
                    typeof(object),
                    interfaces.ToArray());
                Type proxyType = proxyTypeBuilder;
                const FieldAttributes fieldAttributes = FieldAttributes.Private | FieldAttributes.InitOnly;
                FieldBuilder jsonRpcField = proxyTypeBuilder.DefineField("rpc", typeof(JsonRpc), fieldAttributes);
                FieldBuilder optionsField = proxyTypeBuilder.DefineField("options", typeof(JsonRpcProxyOptions), fieldAttributes);
                FieldBuilder ownerField = proxyTypeBuilder.DefineField("owner", typeof(StackTrace), fieldAttributes);

                VerifySupported(!FindAllOnThisAndOtherInterfaces(serviceInterface, i => i.DeclaredProperties).Any(), Resources.UnsupportedPropertiesOnClientProxyInterface, serviceInterface);

                // Implement events
                var ctorActions = new List<Action<ILGenerator>>();
                foreach (EventInfo evt in FindAllOnThisAndOtherInterfaces(serviceInterface, i => i.DeclaredEvents))
                {
                    VerifySupported(evt.EventHandlerType!.Equals(typeof(EventHandler)) || (evt.EventHandlerType.GetTypeInfo().IsGenericType && evt.EventHandlerType.GetGenericTypeDefinition().Equals(typeof(EventHandler<>))), Resources.UnsupportedEventHandlerTypeOnClientProxyInterface, evt);

                    // public event EventHandler EventName;
                    EventBuilder evtBuilder = proxyTypeBuilder.DefineEvent(evt.Name, evt.Attributes, evt.EventHandlerType);

                    // private EventHandler eventName;
                    FieldBuilder evtField = proxyTypeBuilder.DefineField(evt.Name, evt.EventHandlerType, FieldAttributes.Private);

                    // add_EventName
                    var addRemoveHandlerParams = new Type[] { evt.EventHandlerType };
                    const MethodAttributes methodAttributes = MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual;
                    MethodBuilder addMethod = proxyTypeBuilder.DefineMethod($"add_{evt.Name}", methodAttributes, null, addRemoveHandlerParams);
                    ImplementEventAccessor(addMethod.GetILGenerator(), evtField, DelegateCombineMethod);
                    evtBuilder.SetAddOnMethod(addMethod);

                    // remove_EventName
                    MethodBuilder removeMethod = proxyTypeBuilder.DefineMethod($"remove_{evt.Name}", methodAttributes, null, addRemoveHandlerParams);
                    ImplementEventAccessor(removeMethod.GetILGenerator(), evtField, DelegateRemoveMethod);
                    evtBuilder.SetRemoveOnMethod(removeMethod);

                    // void OnEventName(EventArgs args)
                    Type eventArgsType = evt.EventHandlerType.GetTypeInfo().GetDeclaredMethod(nameof(EventHandler.Invoke))!.GetParameters()[1].ParameterType;
                    MethodBuilder raiseEventMethod = proxyTypeBuilder.DefineMethod(
                        $"On{evt.Name}",
                        MethodAttributes.HideBySig | MethodAttributes.Private,
                        null,
                        new Type[] { eventArgsType });
                    ImplementRaiseEventMethod(raiseEventMethod.GetILGenerator(), evtField, jsonRpcField);

                    ctorActions.Add(new Action<ILGenerator>(il =>
                    {
                        MethodInfo addLocalRpcMethod = typeof(JsonRpc).GetRuntimeMethod(nameof(JsonRpc.AddLocalRpcMethod), new Type[] { typeof(string), typeof(Delegate) })!;
                        ConstructorInfo delegateCtor = typeof(Action<>).MakeGenericType(eventArgsType).GetTypeInfo().DeclaredConstructors.Single();

                        // rpc.AddLocalRpcMethod("EventName", new Action<EventArgs>(this.OnEventName));
                        il.Emit(OpCodes.Ldarg_1); // .ctor's rpc parameter

                        // First argument to AddLocalRpcMethod is the method name.
                        // Run it through the method name transform.
                        // this.options.EventNameTransform.Invoke("clrOrAttributedMethodName")
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, optionsField);
                        il.EmitCall(OpCodes.Callvirt, EventNameTransformPropertyGetter, null);
                        il.Emit(OpCodes.Ldstr, evt.Name);
                        il.EmitCall(OpCodes.Callvirt, EventNameTransformInvoke, null);

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldftn, raiseEventMethod);
                        il.Emit(OpCodes.Newobj, delegateCtor);
                        il.Emit(OpCodes.Callvirt, addLocalRpcMethod);
                    }));
                }

                // .ctor(JsonRpc, JsonRpcProxyOptions, StackTrace)
                {
                    ConstructorBuilder ctor = proxyTypeBuilder.DefineConstructor(
                        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                        CallingConventions.Standard,
                        new Type[] { typeof(JsonRpc), typeof(JsonRpcProxyOptions), typeof(StackTrace) });
                    ILGenerator il = ctor.GetILGenerator();

                    // : base()
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, ObjectCtor);

                    // this.rpc = rpc;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, jsonRpcField);

                    // this.options = options;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Stfld, optionsField);

                    // this.owner = owner;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Stfld, ownerField);

                    // Emit IL that supports events.
                    foreach (Action<ILGenerator> action in ctorActions)
                    {
                        action(il);
                    }

                    il.Emit(OpCodes.Ret);
                }

                // IDisposable.Dispose()
                {
                    MethodBuilder disposeMethod = proxyTypeBuilder.DefineMethod(nameof(IDisposable.Dispose), MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual);
                    ILGenerator il = disposeMethod.GetILGenerator();

                    // this.rpc.Dispose();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);
                    MethodInfo jsonRpcDisposeMethod = typeof(JsonRpc).GetTypeInfo().GetDeclaredMethods(nameof(JsonRpc.Dispose)).Single(m => m.GetParameters().Length == 0);
                    il.EmitCall(OpCodes.Callvirt, jsonRpcDisposeMethod, EmptyTypes);

                    // GC.SuppressFinalize(this);
                    il.Emit(OpCodes.Ldarg_0);
                    MethodInfo suppressFinalize = typeof(GC).GetTypeInfo().GetDeclaredMethod(nameof(GC.SuppressFinalize));
                    il.EmitCall(OpCodes.Call, suppressFinalize, EmptyTypes);

                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetTypeInfo().GetDeclaredMethod(nameof(IDisposable.Dispose))!);
                }

                // Finalizer (for abandoned connection detection)
                {
                    MethodBuilder finalizeMethod = proxyTypeBuilder.DefineMethod("Finalize", MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.Virtual);
                    ILGenerator il = finalizeMethod.GetILGenerator();

                    Label endTryLabel = il.DefineLabel();

                    // try {
                    il.BeginExceptionBlock();

                    // We must not dereference any other objects when the AppDomain is being unloaded
                    // since there's no guarantee at that point that references are valid.
                    // if (!AppDomain.CurrentDomain.IsFinalizingForUnload()) {
                    il.Emit(OpCodes.Call, GetCurrentDomainMethod);
                    il.Emit(OpCodes.Callvirt, IsFinalizingForUnloadMethod);
                    il.Emit(OpCodes.Brtrue_S, endTryLabel);
                    {
                        // this.rpc.NotifyAbandonedProxy(this.owner, typeof(ProxyType));
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, jsonRpcField);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, ownerField);
                        il.Emit(OpCodes.Ldtoken, serviceInterface);
                        il.Emit(OpCodes.Call, GetTypeFromHandleMethod);
                        il.EmitCall(OpCodes.Callvirt, NotifyAbandonedProxyMethod, EmptyTypes);
                    }

                    il.MarkLabel(endTryLabel);

                    // } finally {
                    il.BeginFinallyBlock();
                    {
                        // base.Finalize();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Call, ObjectFinalizeMethod);
                    }

                    // } // finally
                    il.EndExceptionBlock();

                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(finalizeMethod, ObjectFinalizeMethod);
                }

                // IJsonRpcClientProxy.JsonRpc property
                {
                    PropertyBuilder jsonRpcProperty = proxyTypeBuilder.DefineProperty(
                        nameof(IJsonRpcClientProxy.JsonRpc),
                        PropertyAttributes.None,
                        typeof(JsonRpc),
                        parameterTypes: null);

                    // get_JsonRpc() method
                    MethodBuilder jsonRpcPropertyGetter = proxyTypeBuilder.DefineMethod(
                        "get_" + nameof(IJsonRpcClientProxy.JsonRpc),
                        MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.SpecialName,
                        typeof(JsonRpc),
                        Type.EmptyTypes);
                    ILGenerator il = jsonRpcPropertyGetter.GetILGenerator();

                    // return this.jsonRpc;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);
                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(jsonRpcPropertyGetter, typeof(IJsonRpcClientProxy).GetTypeInfo().GetDeclaredProperty(nameof(IJsonRpcClientProxy.JsonRpc))!.GetMethod!);
                    jsonRpcProperty.SetGetMethod(jsonRpcPropertyGetter);
                }

                MethodInfo[] invokeAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeAsync) && m.GetParameters()[1].ParameterType == typeof(object[])).ToArray();
                MethodInfo invokeAsyncOfTaskMethodInfo = invokeAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                MethodInfo invokeAsyncOfTaskOfTMethodInfo = invokeAsyncMethodInfos.Single(m => m.IsGenericMethod);

                IEnumerable<MethodInfo> invokeWithCancellationAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeWithCancellationAsync));
                MethodInfo invokeWithCancellationAsyncOfTaskMethodInfo = invokeWithCancellationAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                MethodInfo invokeWithCancellationAsyncOfTaskOfTMethodInfo = invokeWithCancellationAsyncMethodInfos.Single(m => m.IsGenericMethod);

                IEnumerable<MethodInfo> invokeWithParameterObjectAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeWithParameterObjectAsync));
                MethodInfo invokeWithParameterObjectAsyncOfTaskMethodInfo = invokeWithParameterObjectAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                MethodInfo invokeWithParameterObjectAsyncOfTaskOfTMethodInfo = invokeWithParameterObjectAsyncMethodInfos.Single(m => m.IsGenericMethod);

                foreach (MethodInfo method in FindAllOnThisAndOtherInterfaces(serviceInterface, i => i.DeclaredMethods).Where(m => !m.IsSpecialName))
                {
                    bool returnTypeIsTask = method.ReturnType == typeof(Task) || (method.ReturnType.GetTypeInfo().IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
                    bool returnTypeIsValueTask = method.ReturnType == typeof(ValueTask) || (method.ReturnType.GetTypeInfo().IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
                    bool returnTypeIsIAsyncEnumerable = method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);
                    VerifySupported(returnTypeIsTask || returnTypeIsValueTask || returnTypeIsIAsyncEnumerable, Resources.UnsupportedMethodReturnTypeOnClientProxyInterface, method, method.ReturnType.FullName!);
                    VerifySupported(!method.IsGenericMethod, Resources.UnsupportedGenericMethodsOnClientProxyInterface, method);

                    bool hasReturnValue = method.ReturnType.GetTypeInfo().IsGenericType;
                    Type? invokeResultTypeArgument = hasReturnValue
                        ? (returnTypeIsIAsyncEnumerable ? method.ReturnType : method.ReturnType.GetTypeInfo().GenericTypeArguments[0])
                        : null;

                    ParameterInfo[] methodParameters = method.GetParameters();
                    MethodBuilder methodBuilder = proxyTypeBuilder.DefineMethod(
                        method.Name,
                        MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                        method.ReturnType,
                        methodParameters.Select(p => p.ParameterType).ToArray());
                    ILGenerator il = methodBuilder.GetILGenerator();

                    // this.rpc
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);

                    // First argument to InvokeAsync is the method name.
                    // Run it through the method name transform.
                    // this.options.MethodNameTransform.Invoke("clrOrAttributedMethodName")
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, optionsField);
                    il.EmitCall(OpCodes.Callvirt, MethodNameTransformPropertyGetter, null);
                    il.Emit(OpCodes.Ldstr, methodNameMap.GetRpcMethodName(method));
                    il.EmitCall(OpCodes.Callvirt, MethodNameTransformInvoke, null);

                    Label positionalArgsLabel = il.DefineLabel();

                    ParameterInfo cancellationTokenParameter = methodParameters.FirstOrDefault(p => p.ParameterType == typeof(CancellationToken));
                    int argumentCountExcludingCancellationToken = methodParameters.Length - (cancellationTokenParameter != null ? 1 : 0);
                    VerifySupported(cancellationTokenParameter == null || cancellationTokenParameter.Position == methodParameters.Length - 1, Resources.CancellationTokenMustBeLastParameter, method);

                    // if (this.options.ServerRequiresNamedArguments) {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, optionsField);
                    il.EmitCall(OpCodes.Callvirt, ServerRequiresNamedArgumentsPropertyGetter, null);
                    il.Emit(OpCodes.Brfalse_S, positionalArgsLabel);

                    // The second argument is a single parameter object.
                    {
                        if (argumentCountExcludingCancellationToken > 0)
                        {
                            ConstructorInfo paramObjectCtor = CreateParameterObjectType(proxyModuleBuilder, methodParameters.Take(argumentCountExcludingCancellationToken).ToArray(), proxyType);
                            for (int i = 0; i < argumentCountExcludingCancellationToken; i++)
                            {
                                il.Emit(OpCodes.Ldarg, i + 1);
                            }

                            il.Emit(OpCodes.Newobj, paramObjectCtor);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldnull);
                        }

                        MethodInfo invokingMethod = invokeResultTypeArgument != null
                            ? invokeWithParameterObjectAsyncOfTaskOfTMethodInfo.MakeGenericMethod(invokeResultTypeArgument)
                            : invokeWithParameterObjectAsyncOfTaskMethodInfo;
                        if (cancellationTokenParameter != null)
                        {
                            il.Emit(OpCodes.Ldarg, cancellationTokenParameter.Position + 1);
                        }
                        else
                        {
                            il.Emit(OpCodes.Call, CancellationTokenNonePropertyGetter);
                        }

                        il.EmitCall(OpCodes.Callvirt, invokingMethod, null);

                        AdaptReturnType(method, returnTypeIsValueTask, returnTypeIsIAsyncEnumerable, il, invokingMethod, cancellationTokenParameter);

                        il.Emit(OpCodes.Ret);
                    }

                    // The second argument is an array of arguments for the RPC method.
                    il.MarkLabel(positionalArgsLabel);
                    {
                        il.Emit(OpCodes.Ldc_I4, argumentCountExcludingCancellationToken);
                        il.Emit(OpCodes.Newarr, typeof(object));

                        for (int i = 0; i < argumentCountExcludingCancellationToken; i++)
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
                        if (invokeResultTypeArgument != null)
                        {
                            invokingMethod = invokingMethod.MakeGenericMethod(invokeResultTypeArgument);
                        }

                        il.EmitCall(OpCodes.Callvirt, invokingMethod, null);

                        AdaptReturnType(method, returnTypeIsValueTask, returnTypeIsIAsyncEnumerable, il, invokingMethod, cancellationTokenParameter);

                        il.Emit(OpCodes.Ret);
                    }

                    proxyTypeBuilder.DefineMethodOverride(methodBuilder, method);
                }

                generatedType = proxyTypeBuilder.CreateTypeInfo()!;
                GeneratedProxiesByInterface.Add(serviceInterface, generatedType);

#if SaveAssembly
                ((AssemblyBuilder)proxyModuleBuilder.Assembly).Save(proxyModuleBuilder.ScopeName);
                System.IO.File.Move(proxyModuleBuilder.ScopeName, proxyModuleBuilder.ScopeName + ".dll");
#endif
            }

            return generatedType;
        }

        /// <summary>
        /// Converts the value on the stack to one compatible with the method's return type.
        /// </summary>
        /// <param name="method">The interface method that we're generating code for.</param>
        /// <param name="returnTypeIsValueTask"><c>true</c> if the return type is <see cref="ValueTask"/> or <see cref="ValueTask{TResult}"/>; <c>false</c> otherwise.</param>
        /// <param name="returnTypeIsIAsyncEnumerable"><c>true</c> if the return type is <see cref="IAsyncEnumerable{TResult}"/>; <c>false</c> otherwise.</param>
        /// <param name="il">The IL emitter for the method.</param>
        /// <param name="invokingMethod">The Invoke method on <see cref="JsonRpc"/> that IL was just emitted to invoke.</param>
        /// <param name="cancellationTokenParameter">The <see cref="CancellationToken"/> parameter in the proxy method, if there is one.</param>
        private static void AdaptReturnType(MethodInfo method, bool returnTypeIsValueTask, bool returnTypeIsIAsyncEnumerable, ILGenerator il, MethodInfo invokingMethod, ParameterInfo? cancellationTokenParameter)
        {
            if (returnTypeIsValueTask)
            {
                // We must convert the Task or Task<T> returned from JsonRpc into a ValueTask or ValueTask<T>
                il.Emit(OpCodes.Newobj, method.ReturnType.GetTypeInfo().GetConstructor(new Type[] { invokingMethod.ReturnType })!);
            }
            else if (returnTypeIsIAsyncEnumerable)
            {
                // We must convert the Task<IAsyncEnumerable<T>> to IAsyncEnumerable<T>
                // Push a CancellationToken to the stack as well. Use the one this method was given if available, otherwise push CancellationToken.None.
                if (cancellationTokenParameter != null)
                {
                    il.Emit(OpCodes.Ldarg, cancellationTokenParameter.Position + 1);
                }
                else
                {
                    LocalBuilder local = il.DeclareLocal(typeof(CancellationToken));
                    il.Emit(OpCodes.Ldloca, local);
                    il.Emit(OpCodes.Initobj, typeof(CancellationToken));
                    il.Emit(OpCodes.Ldloc, local);
                }

#pragma warning disable CS0618
                MethodInfo createProxyEnumerableMethod = typeof(CodeGenHelpers).GetMethod(nameof(CodeGenHelpers.CreateAsyncEnumerableProxy), BindingFlags.Static | BindingFlags.Public).MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]);
#pragma warning restore CS0618
                il.Emit(OpCodes.Call, createProxyEnumerableMethod);
            }
        }

        /// <summary>
        /// Gets the <see cref="ModuleBuilder"/> to use for generating a proxy for the given type.
        /// </summary>
        /// <param name="interfaceType">The type of the interface to generate a proxy for.</param>
        /// <returns>The <see cref="ModuleBuilder"/> to use.</returns>
        private static ModuleBuilder GetProxyModuleBuilder(TypeInfo interfaceType)
        {
            Requires.NotNull(interfaceType, nameof(interfaceType));
            Assumes.True(Monitor.IsEntered(BuilderLock));

            // Dynamic assemblies are relatively expensive. We want to create as few as possible.
            // For each set of skip visibility check assemblies, we need a dynamic assembly that skips at *least* that set.
            // The CLR will not honor any additions to that set once the first generated type is closed.
            // We maintain a dictionary to point at dynamic modules based on the set of skip visiblity check assemblies they were generated with.
            ImmutableHashSet<AssemblyName> skipVisibilityCheckAssemblies = SkipClrVisibilityChecks.GetSkipVisibilityChecksRequirements(interfaceType)
                .Add(typeof(ProxyGeneration).Assembly.GetName());
            foreach ((ImmutableHashSet<AssemblyName> SkipVisibilitySet, ModuleBuilder Builder) existingSet in TransparentProxyModuleBuilderByVisibilityCheck)
            {
                if (existingSet.SkipVisibilitySet.IsSupersetOf(skipVisibilityCheckAssemblies))
                {
                    return existingSet.Builder;
                }
            }

            // As long as we're going to start a new module, let's maximize the chance that this is the last one
            // by skipping visibility checks on ALL assemblies loaded so far.
            // I have disabled this optimization though till we need it since it would sometimes cover up any bugs in the above visibility checking code.
            ////skipVisibilityCheckAssemblies = skipVisibilityCheckAssemblies.Union(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName()));

            AssemblyBuilder assemblyBuilder = CreateProxyAssemblyBuilder();
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("rpcProxies");
            var skipClrVisibilityChecks = new SkipClrVisibilityChecks(assemblyBuilder, moduleBuilder);
            skipClrVisibilityChecks.SkipVisibilityChecksFor(skipVisibilityCheckAssemblies);
            TransparentProxyModuleBuilderByVisibilityCheck.Add((skipVisibilityCheckAssemblies, moduleBuilder));

            return moduleBuilder;
        }

        private static AssemblyBuilder CreateProxyAssemblyBuilder()
        {
            var proxyAssemblyName = new AssemblyName(string.Format(CultureInfo.InvariantCulture, "rpcProxies_{0}", Guid.NewGuid()));
#if SaveAssembly
            return AssemblyBuilder.DefineDynamicAssembly(proxyAssemblyName, AssemblyBuilderAccess.RunAndSave);
#else
            return AssemblyBuilder.DefineDynamicAssembly(proxyAssemblyName, AssemblyBuilderAccess.RunAndCollect);
#endif
        }

        private static ConstructorInfo CreateParameterObjectType(ModuleBuilder moduleBuilder, ParameterInfo[] parameters, Type parentType)
        {
            Requires.NotNull(parameters, nameof(parameters));
            if (parameters.Length == 0)
            {
                return ObjectCtor;
            }

            TypeBuilder proxyTypeBuilder = moduleBuilder.DefineType(
                string.Format(CultureInfo.InvariantCulture, "_param_{0}", Guid.NewGuid()),
                TypeAttributes.NotPublic);

            var parameterTypes = new Type[parameters.Length];
            var fields = new FieldBuilder[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameterTypes[i] = parameters[i].ParameterType;
                fields[i] = proxyTypeBuilder.DefineField(parameters[i].Name!, parameters[i].ParameterType, FieldAttributes.Public | FieldAttributes.InitOnly);
            }

            ConstructorBuilder ctor = proxyTypeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                parameterTypes);
            for (int i = 0; i < parameters.Length; i++)
            {
                ctor.DefineParameter(i + 1, ParameterAttributes.In, parameters[i].Name);
            }

            ILGenerator il = ctor.GetILGenerator();

            // : base()
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ObjectCtor);

            for (int i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Stfld, fields[i]);
            }

            il.Emit(OpCodes.Ret);

            // Finalize the type
            proxyTypeBuilder.CreateTypeInfo();

            return ctor;
        }

        private static void ImplementRaiseEventMethod(ILGenerator il, FieldBuilder evtField, FieldBuilder jsonRpcField)
        {
            Label retLabel = il.DefineLabel();
            Label invokeLabel = il.DefineLabel();

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
            il.Emit(OpCodes.Callvirt, evtField.FieldType.GetTypeInfo().GetDeclaredMethod(nameof(EventHandler.Invoke))!);

            il.MarkLabel(retLabel);
            il.Emit(OpCodes.Ret);
        }

        private static void ImplementEventAccessor(ILGenerator il, FieldInfo evtField, MethodInfo combineOrRemoveMethod)
        {
            Label loopStart = il.DefineLabel();

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
                MethodInfo compareExchangeClosedGeneric = CompareExchangeMethod.MakeGenericMethod(evtField.FieldType);
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
            Requires.NotNull(problematicMember, nameof(problematicMember));

            if (!condition)
            {
                object[] formattingArgs = new object[1 + otherArgs?.Length ?? 0];
                formattingArgs[0] = problematicMember is TypeInfo problematicType ? problematicType.FullName! : problematicMember.DeclaringType?.FullName + "." + problematicMember.Name;
                if (otherArgs?.Length > 0)
                {
                    Array.Copy(otherArgs, 0, formattingArgs, 1, otherArgs.Length);
                }

                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, messageFormat, formattingArgs));
            }
        }

        private static IEnumerable<T> FindAllOnThisAndOtherInterfaces<T>(TypeInfo interfaceType, Func<TypeInfo, IEnumerable<T>> oneInterfaceQuery)
        {
            Requires.NotNull(interfaceType, nameof(interfaceType));
            Requires.NotNull(oneInterfaceQuery, nameof(oneInterfaceQuery));

            IEnumerable<T> result = oneInterfaceQuery(interfaceType);
            return result.Concat(interfaceType.ImplementedInterfaces.SelectMany(i => oneInterfaceQuery(i.GetTypeInfo())));
        }

        /// <summary>
        /// A synthesized <see cref="IAsyncEnumerable{T}"/> that makes a promise for such a value look like the actual value.
        /// </summary>
        /// <typeparam name="T">The type of element produced by the enumerable.</typeparam>
        internal class AsyncEnumerableProxy<T> : IAsyncEnumerable<T>
        {
            private readonly Task<IAsyncEnumerable<T>> enumerableTask;
            private readonly CancellationToken defaultCancellationToken;

            internal AsyncEnumerableProxy(Task<IAsyncEnumerable<T>> enumerableTask, CancellationToken defaultCancellationToken)
            {
                this.enumerableTask = enumerableTask ?? throw new ArgumentNullException(nameof(enumerableTask));
                this.defaultCancellationToken = defaultCancellationToken;
            }

            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken)
            {
                return new AsyncEnumeratorProxy(this.enumerableTask, cancellationToken.CanBeCanceled ? cancellationToken : this.defaultCancellationToken);
            }

            private class AsyncEnumeratorProxy : IAsyncEnumerator<T>
            {
                private readonly AsyncLazy<IAsyncEnumerator<T>> enumeratorLazy;

                internal AsyncEnumeratorProxy(Task<IAsyncEnumerable<T>> enumerableTask, CancellationToken cancellationToken)
                {
                    this.enumeratorLazy = new AsyncLazy<IAsyncEnumerator<T>>(
                        async () =>
                        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                            IAsyncEnumerable<T> enumerable = await enumerableTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
                            return enumerable.GetAsyncEnumerator(cancellationToken);
                        },
                        joinableTaskFactory: null);
                }

                public T Current
                {
                    get
                    {
                        Verify.Operation(this.enumeratorLazy.IsValueFactoryCompleted, "Await MoveNextAsync first.");
                        return this.enumeratorLazy.GetValue().Current;
                    }
                }

                public async ValueTask<bool> MoveNextAsync()
                {
                    IAsyncEnumerator<T> enumerator = await this.enumeratorLazy.GetValueAsync().ConfigureAwait(false);
                    return await enumerator.MoveNextAsync().ConfigureAwait(false);
                }

                public async ValueTask DisposeAsync()
                {
                    // Even if we haven't been asked for the iterator yet, the server has (or soon may), so we must acquire it
                    // and dispose of it so that we don't incur a memory leak on the server.
                    IAsyncEnumerator<T> enumerator = await this.enumeratorLazy.GetValueAsync().ConfigureAwait(false);
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
