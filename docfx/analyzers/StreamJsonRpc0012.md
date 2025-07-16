# StreamJsonRpc0012: Unsupported member

Interfaces with <xref:StreamJsonRpc.JsonRpcContractAttribute> applied may only declare methods.

## Example violation

The following RPC interface is declared with a property.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0012.cs#Violation)]

## Resolution

Change the property to an async method.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0012.cs#Fix)]
