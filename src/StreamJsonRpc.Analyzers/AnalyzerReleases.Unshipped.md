; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
StreamJsonRpc0001 | Usage | Error | Inaccessible interface
StreamJsonRpc0002 | Usage | Warning | Non-partial interface
StreamJsonRpc0003 | Usage | Warning | Use JsonRpcContractAttribute
StreamJsonRpc0004 | Usage | Error | Use interfaces for proxies
StreamJsonRpc0005 | Usage | Error | RpcMarshalable interfaces must be IDisposable
StreamJsonRpc0006 | Usage | Error | All interfaces in a proxy group must be attributed
StreamJsonRpc0007 | Usage | Error | Use RpcMarshalableAttribute on optional marshalable interface
StreamJsonRpc0008 | Usage | Warning | Add methods to PolyType shape for RPC contract interface
StreamJsonRpc0009 | Usage | Warning | Use GenerateShapeAttribute on optional marshalable interface
StreamJsonRpc0011 | Usage | Error | Unsupported RPC method return type
StreamJsonRpc0012 | Usage | Error | RPC contracts may not include this type of member
StreamJsonRpc0013 | Usage | Error | RPC contracts may not include generic methods
StreamJsonRpc0014 | Usage | Error | CancellationToken may only appear as the last parameter
StreamJsonRpc0015 | Usage | Error | RPC contracts may not be generic interfaces
StreamJsonRpc0016 | Usage | Error | Unsupported event delegate type
StreamJsonRpc0030 | Usage | Error | `JsonRpcProxyAttribute<T>` should be applied only to generic interfaces.
StreamJsonRpc0031 | Usage | Error | `JsonRpcProxyAttribute<T>` type argument should be a closed instance of the applied type.
StreamJsonRpc0032 | Usage | Error | `JsonRpcProxyAttribute<T>` should be accompanied by another contract attribute.
StreamJsonRpc0050 | Usage | Warning | Use IClientProxy.Is or JsonRpcExtensions.As.
