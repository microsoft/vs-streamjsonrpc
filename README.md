StreamJsonRpc
===============

StreamJsonRpc is a cross-platform, .NET portable library that implements the
[JSON-RPC][JSONRPC] wire protocol.

Its transport is a standard System.IO.Stream so you can use it with any transport.

## Compatibility  

This library has been tested with and is compatible with the following other
JSON-RPC libraries:

* [json-rpc-peer][json-rpc-peer] (npm)

## Testability/mockability

Testing this library or users of this library can be done without any transport
by using the [Nerdbank.FullDuplexStream][FullDuplexStream] library in your tests
to produce the Stream object.

[JSONRPC]: http://json-rpc.org/
[json-rpc-peer]: https://www.npmjs.com/package/json-rpc-peer
[FullDuplexStream]: https://www.nuget.org/packages/nerdbank.fullduplexstream
