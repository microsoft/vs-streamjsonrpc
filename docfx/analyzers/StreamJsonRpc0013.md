# StreamJsonRpc0013: No generic methods

Interfaces with <xref:StreamJsonRpc.JsonRpcContractAttribute> applied may not declare generic methods.

## Example violation

The following RPC interface is declared with a generic method.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0013.cs#Violation)]

## Resolution

Change the method to remove the generic type parameter.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0013.cs#Fix)]
