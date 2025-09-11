# StreamJsonRpc0007: Use RpcMarshalableAttribute on optional marshalable interface

An interface specified as an argument to <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> must itself be attributed with <xref:StreamJsonRpc.RpcMarshalableAttribute> with <xref:StreamJsonRpc.RpcMarshalableAttribute.IsOptional> set to true.

## Example violation

The following interfaces are meant to both be RPC marshalable, but only one has the <xref:StreamJsonRpc.RpcMarshalableAttribute>.
The other is designated as optional and thus needs the attribute.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0007.cs#Violation)]

## Resolution

Add <xref:StreamJsonRpc.RpcMarshalableAttribute> to the optional interface, taking care to set <xref:StreamJsonRpc.RpcMarshalableAttribute.IsOptional> to true.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0007.cs#Fix)]
