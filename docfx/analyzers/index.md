# Analyzers

The `StreamJsonRpc` nuget packages comes with C# analyzers to help you author valid code. They will emit diagnostics with warnings or errors depending on the severity of the issue.

Some of these diagnostics will include a suggested code fix that can apply the correction to your code automatically.

Rule ID                                   | Category | Severity | Notes                                  |
------------------------------------------|----------|----------|----------------------------------------|
[StreamJsonRpc0001](StreamJsonRpc0001.md) | Usage    | Error    | RPC methods use supported return types |
