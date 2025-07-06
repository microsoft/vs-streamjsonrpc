# StreamJsonRpc0002: Inaccessible interface

Interfaces with <xref:StreamJsonRpc.RpcContractAttribute> applied must be declared with at least `internal` visibility so that the source generator can implement that interface with an implementing proxy class.

## Example violation

The following RPC interface is declared as a nested, `private` type.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0002.cs#Violation)]

## Resolution

Change the visibility modifier to `internal`.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0002.cs#Fix)]
