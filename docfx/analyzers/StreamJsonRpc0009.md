# StreamJsonRpc0009: Use GenerateShapeAttribute on optional marshalable interface

RPC interfaces attributed with <xref:StreamJsonRpc.RpcMarshalableAttribute> where <xref:StreamJsonRpc.RpcMarshalableAttribute.IsOptional> is `true` should also be attributed for PolyType method shape generation using <xref:PolyType.GenerateShapeAttribute>.
This ensures the interface can be used for RPC target objects in NativeAOT environments and with formatters prepared for those environments such as <xref:StreamJsonRpc.NerdbankMessagePackFormatter>.

This diagnostic may be disabled when running in a trimmed application is not in scope and not using a formatter that requires it, as <xref:StreamJsonRpc.NerdbankMessagePackFormatter> would.

## Example violation

The following interface serves as an optional RPC marshalable interface but uses <xref:PolyType.TypeShapeAttribute> instead of <xref:PolyType.GenerateShapeAttribute>:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0009.cs#Violation)]

## Resolution

Switch <xref:PolyType.TypeShapeAttribute> to <xref:PolyType.GenerateShapeAttribute>:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0009.cs#Fix)]
