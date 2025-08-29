# StreamJsonRpc0031: `[JsonRpcProxy<T>]` type argument should be a closed instance of the applied type

The <xref:StreamJsonRpc.JsonRpcProxyAttribute`1> attribute is intended for closing open generic interfaces that serve as RPC contracts in order to ensure a proxy is available at runtime that implements particular closed instances of those open generic interfaces.

## Example violation

The following RPC contract has an <xref:StreamJsonRpc.JsonRpcProxyAttribute`1> with a type argument that does not match the interface itself:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0031.cs#Violation)]

## Resolution

Adjust the type argument to match the applied interface:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0031.cs#Fix)]
