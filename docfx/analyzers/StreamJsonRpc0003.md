# StreamJsonRpc0003: Use JsonRpcContractAttribute

Interfaces passed to <xref:StreamJsonRpc.JsonRpc.Attach*?displayProperty=nameWithType> methods should be attributed with <xref:StreamJsonRpc.JsonRpcContractAttribute> to enable analyzers that help avoid runtime exceptions, and to source generate proxies that can improve startup performance.

## Example violation

The following code creates a proxy based on an interface that lacks the <xref:StreamJsonRpc.JsonRpcContractAttribute>:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0003.cs#Violation)]

## Resolution

Add the <xref:StreamJsonRpc.JsonRpcContractAttribute> to the interface (and resolve any new diagnostics):

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0003.cs#Fix)]
