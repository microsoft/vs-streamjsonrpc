# StreamJsonRpc0006: All interfaces in a proxy group must be attributed

Interfaces annotated with <xref:StreamJsonRpc.JsonRpcProxyInterfaceGroupAttribute> must also be attributed with <xref:StreamJsonRpc.JsonRpcContractAttribute>.
All interfaces listed as attribute arguments must also be attributed with <xref:StreamJsonRpc.JsonRpcContractAttribute>.

## Example violation

The following interface has a <xref:StreamJsonRpc.JsonRpcProxyInterfaceGroupAttribute> but no <xref:StreamJsonRpc.JsonRpcContractAttribute> on itself or the other interface in its group:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0006.cs#Violation)]

## Resolution

Add <xref:StreamJsonRpc.JsonRpcContractAttribute> to the interface the attribute is applied to, and each interface listed in the attribute arguments list:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0006.cs#Fix)]
