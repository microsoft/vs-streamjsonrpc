# NativeAOT / Trimming

StreamJsonRpc is partially NativeAOT safe.

A consuming application can target NativeAOT while referencing StreamJsonRpc by observing these guidelines and restrictions:

1. Set the `EnableStreamJsonRpcInterceptors` MSBuild property to `true` in the project files covering all uses of <xref:StreamJsonRpc.JsonRpc.Attach*?displayProperty=nameWithType>.
   This includes those of your dependencies as well.
   Activating this feature will cause requests for proxies to fail _if_ those requests include interfaces for which proxies have not been source generated.
   The source generator can predict these types in limited cases, and by default any <xref:StreamJsonRpc.JsonRpcContractAttribute> interface will have a proxy generated for it.

   Use the <xref:StreamJsonRpc.JsonRpcProxyInterfaceGroupAttribute> to specify sets of interfaces that should be supported.

   Set the <xref:StreamJsonRpc.JsonRpcProxyOptions.AcceptProxyWithExtraInterfaces?displayProperty=nameWithType> property to `true` to reduce the number of predefined groups for which proxies must be specially generated.
1. Use <xref:StreamJsonRpc.SystemTextJsonFormatter> instead of the default <xref:StreamJsonRpc.JsonMessageFormatter>.
1. Set <xref:System.Text.Json.JsonSerializerOptions.TypeInfoResolver> on the <xref:StreamJsonRpc.SystemTextJsonFormatter.JsonSerializerOptions?displayProperty=nameWithType> property to the `Default` property on your class that derives from <xref:System.Text.Json.Serialization.JsonSerializerContext>.
1. Use <xref:StreamJsonRpc.JsonRpc.AddLocalRpcTarget(StreamJsonRpc.RpcTargetMetadata,System.Object,StreamJsonRpc.JsonRpcTargetOptions)?displayProperty=nameWithType> to add RPC target objects rather than other overloads.
1. When constructing proxies, use the <xref:StreamJsonRpc.JsonRpc.Attach*> methods with `typeof` arguments or specific generic type arguments.
1. When using named parameters (e.g. <xref:StreamJsonRpc.JsonRpc.NotifyWithParameterObjectAsync*> or <xref:StreamJsonRpc.JsonRpc.InvokeWithParameterObjectAsync*>), call the overloads that accept <xref:StreamJsonRpc.NamedArgs>.
1. Avoid [RPC marshalable objects](../exotic_types/rpc_marshalable_objects.md).

## Sample program

The following program can execute in a NativeAOT published application:

### UTF-8 JSON

The <xref:StreamJsonRpc.SystemTextJsonFormatter> provides a semi-safe NativeAOT experience for those that require UTF-8 encoded JSON:

[!code-csharp[](../../samples/NativeAOT/SystemTextJson.cs#Sample)]
