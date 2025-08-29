# StreamJsonRpc0030: `[JsonRpcProxy<T>]` should be applied only to generic interfaces

The <xref:StreamJsonRpc.JsonRpcProxyAttribute`1> attribute is intended for closing open generic interfaces that serve as RPC contracts in order to ensure a proxy is available at runtime that implements particular closed instances of those open generic interfaces.

## Example violation

The following RPC contract is not generic, yet applies this attribute:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0030.cs#Violation)]

## Resolution

Drop the unnecessary attribute:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0030.cs#Fix)]
