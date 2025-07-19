# StreamJsonRpc0014: CancellationToken as last parameter

Interfaces with <xref:StreamJsonRpc.JsonRpcContractAttribute> applied may declare methods with <xref:System.Threading.CancellationToken> only when they appear as the last parameter.

## Example violation

The following RPC interface declares a method with <xref:System.Threading.CancellationToken> appearing as a parameter in the middle of the parameter list.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0014.cs#Violation)]

## Resolution

Move the <xref:System.Threading.CancellationToken> parameter to the end of the parameter list.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0014.cs#Fix)]
