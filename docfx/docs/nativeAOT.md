# NativeAOT / Trimming

StreamJsonRpc is partially NativeAOT safe.

A consuming application can target NativeAOT while referencing StreamJsonRpc by observing these guidelines and restrictions:

1. Use the <xref:StreamJsonRpc.SystemTextJsonFormatter> instead of the default one.
1. Set <xref:System.Text.Json.JsonSerializerOptions.TypeInfoResolver> on the <xref:StreamJsonRpc.SystemTextJsonFormatter.JsonSerializerOptions?displayProperty=nameWithType> property to the `Default` property on your class that derives from <xref:System.Text.Json.Serialization.JsonSerializerContext>.
1. Use <xref:StreamJsonRpc.JsonRpc.AddLocalRpcMethod*> instead of <xref:StreamJsonRpc.JsonRpc.AddLocalRpcTarget*>.
1. When constructing proxies, use the <xref:StreamJsonRpc.JsonRpc.Attach*> methods with `typeof` arguments or specific generic type arguments.
1. Avoid [RPC marshalable objects](../exotic_types/rpc_marshalable_objects.md).

## Sample program

The following program can execute in a NativeAOT published application:

[!code-csharp[](../../samples/NativeAOT.cs#STJSample)]
