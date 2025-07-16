# StreamJsonRpc0015: No generic interfaces

Interfaces with <xref:StreamJsonRpc.JsonRpcContractAttribute> applied may not be declared as generic.

## Example violation

The following RPC interface is a generic interface.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0015.cs#Violation)]

## Resolution

Remove the generic type parameter.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0015.cs#Fix)]
