; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
StreamJsonRpc0010 | Usage | Error | Inaccessible interface
StreamJsonRpc0011 | Usage | Error | Unsupported RPC method return type
StreamJsonRpc0012 | Usage | Error | RPC contracts may not include this type of member
StreamJsonRpc0013 | Usage | Error | RPC contracts may not include generic methods
StreamJsonRpc0014 | Usage | Error | CancellationToken may only appear as the last parameter
