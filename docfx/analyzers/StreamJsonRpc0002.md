# StreamJsonRpc0002: Declare partial interfaces

Interfaces with <xref:StreamJsonRpc.RpcContractAttribute> applied should be declared as `partial`, and all its containing types should also be declared as `partial`, so that the source generator may apply attributes to the interface that enables runtime discovery of the source generated proxy class

## Example violation

The following RPC interface is declared without adequate `partial` modifiers:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0002.cs#Violation)]

## Resolution

Add `partial` to the interface any all containing types.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0002.cs#Fix)]
