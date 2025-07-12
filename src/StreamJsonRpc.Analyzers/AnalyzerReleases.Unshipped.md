; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

| Rule ID           | Category | Severity | Notes                                                   |
| ----------------- | -------- | -------- | ------------------------------------------------------- |
| StreamJsonRpc0002 | Usage    | Warning  | Non-partial interface                                   |
| StreamJsonRpc0001 | Usage    | Error    | Inaccessible interface                                  |
| StreamJsonRpc0011 | Usage    | Error    | Unsupported RPC method return type                      |
| StreamJsonRpc0012 | Usage    | Error    | RPC contracts may not include this type of member       |
| StreamJsonRpc0013 | Usage    | Error    | RPC contracts may not include generic methods           |
| StreamJsonRpc0014 | Usage    | Error    | CancellationToken may only appear as the last parameter |
| StreamJsonRpc0015 | Usage    | Error    | RPC contracts may not be generic interfaces             |
| StreamJsonRpc0016 | Usage    | Error    | Unsupported event delegate type                         |
