# StreamJsonRpc0032: `[JsonRpcProxy<T>]` should be accompanied by another contract attribute

The <xref:StreamJsonRpc.JsonRpcProxyAttribute`1> attribute is intended for closing open generic interfaces that serve as RPC contracts in order to ensure a proxy is available at runtime that implements particular closed instances of those open generic interfaces.

## Example violation

The following interface has an <xref:StreamJsonRpc.JsonRpcProxyAttribute`1> applied but no RPC contract attribute:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0032.cs#Violation)]

## Resolution

Add either <xref:StreamJsonRpc.JsonRpcContractAttribute> or <xref:StreamJsonRpc.RpcMarshalableAttribute> to the interface:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0032.cs#Fix)]
