# StreamJsonRpc0010: Avoid ambiguous RPC method overloads

Methods declared on an RPC interface should have signatures that the server can distinguish without attempting to deserialize arguments into multiple candidate parameter types.

Two methods are considered ambiguous when:

- Their convention-derived RPC names match after removing a trailing `Async`. Names explicitly specified by <xref:StreamJsonRpc.JsonRpcMethodAttribute> or <xref:PolyType.MethodShapeAttribute.Name> are compared verbatim.
- Their supported positional argument counts overlap. Optional parameters define the minimum and maximum accepted counts, and <xref:System.Threading.CancellationToken> parameters are excluded.

Parameter names and types do not resolve this warning because positional JSON-RPC arguments include neither. StreamJsonRpc may try deserializing arguments into each candidate overload, but the first overload that succeeds is not a reliable way to select a method and adds avoidable overhead.

## Example violation

Both overloads below accept two serialized arguments.

[!code-csharp[](../../samples/cs/Analyzers/StreamJsonRpc0010.cs#Violation)]

## Resolution

Give each method a distinct RPC name, either by renaming the method or applying <xref:StreamJsonRpc.JsonRpcMethodAttribute>. Alternatively, change the signatures so their supported argument counts do not overlap.

> [!CAUTION]
> Changing a method name, RPC name, or signature changes the RPC protocol and may break communication when the client and server use different contract versions. Coordinate or version these changes when independently deployed peers may not upgrade together.

[!code-csharp[](../../samples/cs/Analyzers/StreamJsonRpc0010.cs#Fix)]
