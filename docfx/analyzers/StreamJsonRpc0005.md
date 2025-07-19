# StreamJsonRpc0005: RpcMarshalable interfaces must be IDisposable

Interfaces annotated with <xref:StreamJsonRpc.RpcMarshalableAttribute> must derive from <xref:System.IDisposable>
unless the attribute sets <xref:StreamJsonRpc.RpcMarshalableAttribute.CallScopedLifetime> to `true`.

## Example violation

The following RPC marshalable interface does not derive from <xref:System.IDisposable>:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0005.cs#Violation)]

## Resolution

Add <xref:System.IDisposable> as a base type:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0005.cs#Fix1)]

Or indicate that the object lifetime is scoped to the call:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0005.cs#Fix2)]
