# StreamJsonRpc

[![NuGet package](https://img.shields.io/nuget/v/StreamJsonRpc.svg)](https://nuget.org/packages/StreamJsonRpc)
[![Build Status](https://dev.azure.com/azure-public/vside/_apis/build/status/vs-streamjsonrpc)](https://dev.azure.com/azure-public/vside/_build/latest?definitionId=13)
[![codecov](https://codecov.io/gh/Microsoft/vs-streamjsonrpc/branch/master/graph/badge.svg)](https://codecov.io/gh/Microsoft/vs-streamjsonrpc)
[![Join the chat at https://gitter.im/vs-streamjsonrpc/Lobby](https://badges.gitter.im/vs-streamjsonrpc/Lobby.svg)](https://gitter.im/vs-streamjsonrpc/Lobby?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

StreamJsonRpc is a cross-platform, .NET portable library that implements the
[JSON-RPC][JSONRPC] wire protocol.

It works over [Stream](https://docs.microsoft.com/en-us/dotnet/api/system.io.stream), [WebSocket](https://docs.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket), or System.IO.Pipelines pipes, independent of the underlying transport.

Bonus features beyond the JSON-RPC spec include:

1. Request cancellation
1. .NET Events as notifications
1. Dynamic client proxy generation
1. Support for [compact binary serialization](doc/extensibility.md) (e.g. MessagePack)

Learn about the use cases for JSON-RPC and how to use this library from our [documentation](doc/index.md).

## Supported platforms

* .NET Standard 2.0 (e.g. .NET Framework 4.7.2, .NET Core 2.1+)

## Compatibility

This library has been tested with and is compatible with the following other
JSON-RPC libraries:

* [json-rpc-peer][json-rpc-peer] (npm)
* [vscode-jsonrpc][vscode-jsonrpc] (npm)

[JSONRPC]: http://jsonrpc.org/
[json-rpc-peer]: https://www.npmjs.com/package/json-rpc-peer
[vscode-jsonrpc]: https://www.npmjs.com/package/vscode-jsonrpc
