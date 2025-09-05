# StreamJsonRpc0008: Add methods to PolyType shape for RPC contract interface

RPC interfaces attributed with <xref:StreamJsonRpc.JsonRpcContractAttribute> or <xref:StreamJsonRpc.RpcMarshalableAttribute> should also be attributed for PolyType method shape generation.
This ensures the interface can be used for RPC target objects in NativeAOT environments and with formatters prepared for those environments such as <xref:StreamJsonRpc.NerdbankMessagePackFormatter>.

This diagnostic may be disabled when running in a trimmed application is not in scope and not using a formatter that requires it, such as the <xref:StreamJsonRpc.NerdbankMessagePackFormatter>.

## Example violation

The following interface serves as an RPC interface but has no PolyType shape with methods included:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0008.cs#Violation)]

## Resolution

Add a <xref:PolyType.TypeShapeAttribute> to the interface and set its <xref:PolyType.TypeShapeAttribute.IncludeMethods> named argument to <xref:PolyType.MethodShapeFlags.PublicInstance> (or a superset of that).

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0008.cs#Fix)]
