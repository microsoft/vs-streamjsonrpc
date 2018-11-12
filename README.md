StreamJsonRpc
===============

[![NuGet package](https://img.shields.io/nuget/v/StreamJsonRpc.svg)](https://nuget.org/packages/StreamJsonRpc)
[![Build Status](https://dev.azure.com/azure-public/vside/_apis/build/status/vs-streamjsonrpc)](https://dev.azure.com/azure-public/vside/_build/latest?definitionId=13)
[![codecov](https://codecov.io/gh/Microsoft/vs-streamjsonrpc/branch/master/graph/badge.svg)](https://codecov.io/gh/Microsoft/vs-streamjsonrpc)
[![Join the chat at https://gitter.im/vs-streamjsonrpc/Lobby](https://badges.gitter.im/vs-streamjsonrpc/Lobby.svg)](https://gitter.im/vs-streamjsonrpc/Lobby?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

StreamJsonRpc is a cross-platform, .NET portable library that implements the
[JSON-RPC][JSONRPC] wire protocol.

It works over [Stream](https://docs.microsoft.com/en-us/dotnet/api/system.io.stream) or [WebSocket](https://docs.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket) independent of the underlying transport.

## Supported platforms

* .NET 4.5
* Windows 8
* Windows Phone 8.1
* .NET Portable (Profile111)
* .NET Standard 1.1

## Compatibility

This library has been tested with and is compatible with the following other
JSON-RPC libraries:

* [json-rpc-peer][json-rpc-peer] (npm)

## Documentation
[Documentation](doc/index.md)

## Testability/mockability

Testing this library or users of this library can be done without any transport
by using the [Nerdbank.FullDuplexStream][FullDuplexStream] library in your tests
to produce the Stream object.

[JSONRPC]: http://jsonrpc.org/
[json-rpc-peer]: https://www.npmjs.com/package/json-rpc-peer
[FullDuplexStream]: https://www.nuget.org/packages/nerdbank.fullduplexstream
