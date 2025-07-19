# StreamJsonRpc0004: Use interfaces for proxies

Types passed to <xref:StreamJsonRpc.JsonRpc.Attach*?displayProperty=nameWithType> methods must be interfaces that are not open generic types.

## Example violation

The following code attempts to create a proxy based on a class type:

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0004.cs#Violation)]

## Resolution

Use an interface instead (and resolve additional warnings that may then appear):

[!code-csharp[](../../samples/Analyzers/StreamJsonRpc0004.cs#Fix)]
