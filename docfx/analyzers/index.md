# Analyzers

The `StreamJsonRpc` nuget packages comes with C# analyzers to help you author valid code. They will emit diagnostics with warnings or errors depending on the severity of the issue.

Some of these diagnostics will include a suggested code fix that can apply the correction to your code automatically.

| Rule ID                                   | Category | Severity | Notes                                                           |
| ----------------------------------------- | -------- | -------- | --------------------------------------------------------------- |
| [StreamJsonRpc0001](StreamJsonRpc0001.md) | Usage    | Error    | Inaccessible interface                                          |
| [StreamJsonRpc0002](StreamJsonRpc0002.md) | Usage    | Warning  | Non-partial interface                                           |
| [StreamJsonRpc0003](StreamJsonRpc0003.md) | Usage    | Warning  | Use JsonRpcContractAttribute                                    |
| [StreamJsonRpc0004](StreamJsonRpc0004.md) | Usage    | Warning  | Use interfaces for proxies                                      |
| [StreamJsonRpc0005](StreamJsonRpc0005.md) | Usage    | Error    | RpcMarshalable interfaces must be IDisposable                   |
| [StreamJsonRpc0006](StreamJsonRpc0006.md) | Usage    | Error    | All interfaces in a proxy group must be attributed              |
| [StreamJsonRpc0011](StreamJsonRpc0011.md) | Usage    | Error    | RPC methods use supported return types                          |
| [StreamJsonRpc0012](StreamJsonRpc0012.md) | Usage    | Error    | Unsupported member                                              |
| [StreamJsonRpc0013](StreamJsonRpc0013.md) | Usage    | Error    | No generic methods                                              |
| [StreamJsonRpc0014](StreamJsonRpc0014.md) | Usage    | Error    | CancellationToken as last parameter                             |
| [StreamJsonRpc0015](StreamJsonRpc0015.md) | Usage    | Error    | No generic interfaces                                           |
| [StreamJsonRpc0016](StreamJsonRpc0016.md) | Usage    | Error    | Unsupported event delegate type                                 |
