# Protocol details and customizations

By default, StreamJsonRpc follows the JSON-RPC protocol's use of textual JSON as the representation of its messages. This provides for maximum interoperability and conformance to the spec.

For performance reasons, it may be appropriate to switch out the textual JSON representation for something that can be serialized faster and/or in a more compact format.

StreamJsonRpc has extensibility points that put you control of multiple levels of the protocol. These extensibility points are described below, including the implementations that are included in the library for your use or replacement.

## Message handlers

The JSON-RPC spec does not describe how this should be done. Since common StreamJsonRpc scenarios include durable connections over which many messages pass, we need to define a way to read and write individual messages over the same connection.

The `IJsonRpcMessageHandler` interface that StreamJsonRpc defines fills the responsibility for writing and reading multiple distinct messages on a single channel.
Some of the constructors on the `JsonRpc` class accept an object that implements this interface, allowing you to define this part of the protocol yourself.

A few `IJsonRpcMessageHandler` implementations are included with this library:

1. `HeaderDelimitedMessageHandler` - This is the default handler. It is compatible with the [`vscode-jsonrpc`](https://www.npmjs.com/package/vscode-jsonrpc) NPM package. It utilizes HTTP-like headers to introduce each JSON-RPC message by describing its length and text encoding. This handler works with .NET `Stream` and the new pipelines API.
1. `LengthHeaderMessageHandler` - This prepends a big endian 32-bit integer to each message to describe the length of the message. This handler works with .NET `Stream` and the new pipelines API. This handler is the fastest handler for those transports.
1. `WebSocketMessageHandler` - This is designed specifically for `WebSocket`, which has implicit message boundaries as part of the web socket protocol. As such, no header is added. Each message is transmitted as a single web socket message.

## Message formatters

When the content of a JSON-RPC message is actually written by a handler, the actual serialization of the message itself is handed off to an implementation of `IJsonRpcMessageFormatter`. This interface has the very focused responsibility serializing and deserializing `JsonRpcMessage`-derived types, of which there are three: `JsonRpcRequest`, `JsonRpcResult` and `JsonRpcError`.

### JsonMessageFormatter

The `JsonMessageFormatter` class implements the `IJsonRpcMessageFormatter` interface by utilizing Newtonsoft.Json to serialize each JSON-RPC message as actual JSON.

Objects you transmit as RPC method arguments or return values must be serializable by Newtonsoft.Json. You can leverage `JsonConverter` and add your custom converters via attributes or by contributing them to the `JsonMessageFormatter.JsonSerializer.Converters` collection.

### MessagePack

The [MessagePack](https://msgpack.org/) protocol is a fast, binary serialization format that resembles the structure of JSON. It can be used as a substitute for JSON when both parties agree on the protocol for significant wins in terms of performance and payload size.

We recommend the [MessagePack for C#](https://www.nuget.org/packages/messagepack) implementation of MessagePack because of its incredibly high serialization speed and more compact message sizes.

Utilizing `MessagePack` for exchanging JSON-RPC messages is incredibly easy. Check out our [MessagePack sample][Sample], which is implemented in terms of a small set of unit tests that demonstrate its usage. While the sample defines the `IJsonRpcMessageFormatter`-derived type that is implemented in terms of MessagePack, this class is not included in the StreamJsonRpc library, but you can use the sample as a starting point for using MessagePack with StreamJsonRpc in your application today.

[Sample]: ../src/StreamJsonRpc.Tests/MessagePackFormatter.cs
