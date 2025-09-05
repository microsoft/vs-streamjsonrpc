# StreamJsonRpc0050: Use `IClientProxy.Is` or `JsonRpcExtensions.As`

When casting or type checking between two <xref:StreamJsonRpc.RpcMarshalableAttribute>-annotated interfaces, it is preferable to use the <xref:StreamJsonRpc.IClientProxy.Is(System.Type)?displayProperty=nameWithType> or <xref:StreamJsonRpc.JsonRpcExtensions.As``1(StreamJsonRpc.IClientProxy)?displayProperty=nameWithType> methods rather than a direct cast or traditional type check.
This is because the interface may be implemented by a proxy object that implements more interfaces than the marshaled object actually implements.
Using these methods informs the caller as to the actual interfaces that are supported by the remote object.

## Example violation

The following code uses various traditional casts and type checks between two marshalable interfaces:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0050.cs#Violation)]

## Resolution

Use the <xref:StreamJsonRpc.IClientProxy.Is(System.Type)> or <xref:StreamJsonRpc.JsonRpcExtensions.As``1(StreamJsonRpc.IClientProxy)> methods instead of traditional casts and type checks.
In fact these are exposed as extension methods on each of the marshalable interfaces, so you can call them directly.

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0050.cs#Fix)]

These extension methods are implemented to use the <xref:StreamJsonRpc.IClientProxy> interface on the same object to test for interface availability on the server, but will gracefully fallback to traditional type checks if <xref:StreamJsonRpc.IClientProxy> is not implemented by the same object, making these extension methods safe to call even when the interfaces are implemented by a user-defined type.

If proxies are generated for these interfaces by any other system, that proxy should also implement <xref:StreamJsonRpc.IClientProxy> to participate in similar dynamic type checking.
