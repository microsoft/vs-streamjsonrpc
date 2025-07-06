# StreamJsonRpc0001: RPC methods use supported return types

Methods declared on an RPC interface (including those annotated with <xref:StreamJsonRpc.RpcContractAttribute>) may only return one of the following types:

- <xref:System.Threading.Tasks.Task>
- <xref:System.Threading.Tasks.Task`1>
- <xref:System.Threading.Tasks.ValueTask>
- <xref:System.Threading.Tasks.ValueTask`1>
- <xref:System.Collections.Generic.IAsyncEnumerable`1>
- `void`

## Example violation

In the following RPC interface, a method returns a disallowed type.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0001.cs#Violation)]

## Resolution

Wrap the return type in <xref:System.Threading.Tasks.Task`1> or <xref:System.Threading.Tasks.ValueTask`1>, which are supported types and allow for RPC to be completed asynchronously.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0001.cs#Fix)]
