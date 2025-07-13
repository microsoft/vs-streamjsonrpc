# StreamJsonRpc0100: Use RpcContractAttribute with AllowAddingMembersLaterAttribute

An <xref:StreamJsonRpc.AllowAddingMembersLaterAttribute> applied to an interface that does not also have <xref:StreamJsonRpc.RpcContractAttribute> applied has no meaning.

## Example violation

The following RPC interface has <xref:StreamJsonRpc.AllowAddingMembersLaterAttribute> but no <xref:StreamJsonRpc.RpcContractAttribute>:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0100.cs#Violation)]

## Resolution

Add <xref:StreamJsonRpc.RpcContractAttribute> or remove <xref:StreamJsonRpc.AllowAddingMembersLaterAttribute>:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0100.cs#Fix)]
