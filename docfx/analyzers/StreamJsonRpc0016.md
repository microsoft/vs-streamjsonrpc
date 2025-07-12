# StreamJsonRpc0016: Unsupported event delegate type

Interfaces with <xref:StreamJsonRpc.RpcContractAttribute> applied may declare events only with <xref:System.EventHandler> or <xref:System.EventHandler`1> delegate types.

## Example violation

The following RPC interface declares an event with a custom delegate:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0016.cs#Violation)]

## Resolution

Use <xref:System.EventHandler> or <xref:System.EventHandler`1> instead.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0016.cs#Fix)]
