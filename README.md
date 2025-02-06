# vs-StreamJsonRpc

[![codecov](https://codecov.io/gh/Microsoft/vs-streamjsonrpc/branch/main/graph/badge.svg)](https://codecov.io/gh/Microsoft/vs-streamjsonrpc)

## StreamJsonRpc

[![NuGet package](https://img.shields.io/nuget/v/StreamJsonRpc.svg)](https://www.nuget.org/packages/StreamJsonRpc)
[![Build Status](https://dev.azure.com/azure-public/vside/_apis/build/status/vs-streamjsonrpc)](https://dev.azure.com/azure-public/vside/_build/latest?definitionId=13)

StreamJsonRpc is a cross-platform, .NET portable library that implements the
[JSON-RPC][JSONRPC] wire protocol.

It works over [Stream](https://docs.microsoft.com/dotnet/api/system.io.stream), [WebSocket](https://docs.microsoft.com/dotnet/api/system.net.websockets.websocket), or System.IO.Pipelines pipes, independent of the underlying transport.

Bonus features beyond the JSON-RPC spec include:

1. Request cancellation
1. .NET Events as notifications
1. Dynamic client proxy generation
1. Support for [compact binary serialization](doc/extensibility.md) via MessagePack
1. Pluggable architecture for custom message handling and formatting.

Learn about the use cases for JSON-RPC and how to use this library from our [documentation](doc/index.md).

[JSONRPC]: https://www.jsonrpc.org/
