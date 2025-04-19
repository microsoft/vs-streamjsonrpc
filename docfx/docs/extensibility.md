# Protocol extensibility

The [JSON-RPC spec][spec] is silent on the subject of actual binary representation of JSON-RPC messages.
This leaves wide open a few important questions:

1. What transport can JSON-RPC messages be sent over?
1. How to delineate messages on a transport that transmits multiple messages?
1. How to encode JSON-RPC messages (e.g. UTF8, UTF16)?

These questions have multiple answers.
StreamJsonRpc implements reasonable and flexible answers to these questions so that in most cases,
you can just use the library without thinking about these details.

StreamJsonRpc has extensibility points that allow you to control multiple levels of the protocol,
including answers to the above questions.
These extensibility points are described below, including the implementations that are included in
the library for your use or replacement.

## Message handlers

StreamJsonRpc is designed to work on any transport (e.g. Web Sockets, IPC pipes,
unix domain sockets, .NET streams). It is designed primarily for full duplex (i.e. bidirectional)
transports that are long-lived rather than request-response transports like HTTP, although HTTP
could theoretically be used as well.

The JSON-RPC spec does not specify a means to delineate the boundary of JSON-RPC messages when a transport may
be used to transmit multiple messages. Although each message is a complete JSON object and a parser might easily
detect the boundary between messages by looking for these objects, in practice many popular JSON parsers are
optimized for parsing an in-memory buffer as a single object rather than asynchronously reading from a stream
of many objects, proffering each object as it goes along.

This has led most JSON-RPC libraries to rely on another protocol to carry each JSON-RPC message as its payload.
For example, web sockets define an implicit message boundary as part of the transport. Another example is the
[Language Server Protocol Specification](https://microsoft.github.io/language-server-protocol/specification),
which typically is used over pipes, and defines an HTTP-like protocol to carry each JSON-RPC message.

The @StreamJsonRpc.IJsonRpcMessageHandler interface that StreamJsonRpc defines fills the responsibility for writing and reading
multiple distinct messages on a transport. Note the transport and the message boundary problem are both answered by
this interface since (as mentioned above) sometimes the transport itself solves the message boundary problem.

StreamJsonRpc includes a few @StreamJsonRpc.IJsonRpcMessageHandler implementations:

1. <xref:StreamJsonRpc.HeaderDelimitedMessageHandler> - This is the default handler. It is compatible with the
   [`vscode-jsonrpc`](https://www.npmjs.com/package/vscode-jsonrpc) NPM package. It utilizes HTTP-like headers to
   introduce each JSON-RPC message by describing its length and (optionally) its text encoding. This handler works
   with .NET @System.IO.Stream and the new [pipelines API](https://devblogs.microsoft.com/dotnet/system-io-pipelines-high-performance-io-in-net/).
1. <xref:StreamJsonRpc.LengthHeaderMessageHandler> - This prepends a big endian 32-bit integer to each message to describe the length
   of the message. This handler works with .NET @System.IO.Stream and the new pipelines API. This handler is the fastest
   handler for those transports.
1. <xref:StreamJsonRpc.NewLineDelimitedMessageHandler> - This appends each JSON-RPC message with `\r\n` or `\n`.
   It should only be used with UTF-8 text-based formatters that do not emit new line characters as part of the JSON.
   This handler works with .NET @System.IO.Stream and the new pipelines API.
1. <xref:StreamJsonRpc.WebSocketMessageHandler> - This is designed specifically for @System.Net.WebSockets.WebSocket, which has implicit message boundaries
   as part of the web socket protocol. As such, no header is added. Each message is transmitted as a single web socket
   message.
1. `HttpClientMessageHandler` - This is a client-side implementation of the
   [JSON-RPC over HTTP spec](https://www.jsonrpc.org/historical/json-rpc-over-http.html), delivered as
   [a sample included in our test project](https://github.com/microsoft/vs-streamjsonrpc/blob/main/test/StreamJsonRpc.Tests/Samples/HttpClientMessageHandler.cs).

Some of the constructors on the @StreamJsonRpc.JsonRpc class accept an object that implements the @StreamJsonRpc.IJsonRpcMessageHandler interface,
allowing you to select any of the built-in behaviors listed above, or define this part of the protocol yourself.

## Message formatters

The JSON-RPC spec is silent on the question of text encoding (e.g. UTF8, UTF16), yet both parties in a JSON-RPC connection
must agree on the encoding in order to communicate. StreamJsonRpc allows any encoding via an extensibility point.

The encoding of an individual JSON-RPC message is the responsibility of an <xref:StreamJsonRpc.IJsonRpcMessageFormatter>.
This interface has the very focused responsibility serializing and deserializing <xref:StreamJsonRpc.Protocol.JsonRpcMessage>-derived types,
of which there are three: <xref:StreamJsonRpc.Protocol.JsonRpcRequest>, <xref:StreamJsonRpc.Protocol.JsonRpcResult> and <xref:StreamJsonRpc.Protocol.JsonRpcError>.

Text-based formatters should implement <xref:StreamJsonRpc.IJsonRpcMessageTextFormatter> (which derives from <xref:StreamJsonRpc.IJsonRpcMessageFormatter>)
so that it can be used with an @StreamJsonRpc.IJsonRpcMessageHandler such as <xref:StreamJsonRpc.HeaderDelimitedMessageHandler> that can determine the
text encoding to use at the transport level.
Interop with other parties is most likely with a UTF-8 text encoding of JSON-RPC messages.

StreamJsonRpc includes the following <xref:StreamJsonRpc.IJsonRpcMessageFormatter> implementations:

1. @StreamJsonRpc.JsonMessageFormatter - Uses Newtonsoft.Json to serialize each JSON-RPC message as actual JSON.
    The text encoding is configurable via a property.
    All RPC method parameters and return types must be serializable by Newtonsoft.Json.
    You can leverage `JsonConverter` and add your custom converters via attributes or by
    contributing them to the `JsonMessageFormatter.JsonSerializer.Converters` collection.

1. <xref:StreamJsonRpc.MessagePackFormatter> - Uses the [MessagePack-CSharp][MessagePackLibrary] library to serialize each
    JSON-RPC message using the very fast and compact binary [MessagePack format][MessagePackFormat].
    All RPC method parameters and return types must be serializable by `IMessagePackFormatter<T>`.
    You can contribute your own via `MessagePackFormatter.SetOptions(MessagePackSerializationOptions)`.
    See alternative formatters below.

1. <xref:StreamJsonRpc.SystemTextJsonFormatter> - Uses the [`System.Text.Json` library][SystemTextJson] to serialize each
    JSON-RPC message as UTF-8 encoded JSON.
    All RPC method parameters and return types must be serializable by System.Text.Json.
    You can leverage `JsonConverter<T>` and add your custom converters via attributes or by
    contributing them to the <xref:StreamJsonRpc.SystemTextJsonFormatter.JsonSerializerOptions?displayProperty=nameWithType>.<xref:System.Text.Json.JsonSerializerOptions.Converters> collection.

When authoring a custom <xref:StreamJsonRpc.IJsonRpcMessageFormatter> implementation, consider supporting the [exotic types](../exotic_types/index.md) that require formatter participation.
We have helper classes to make this straightforward.
Refer to the source code from our built-in formatters to see how to use these helper classes.

### Choosing your formatter

#### When to use <xref:StreamJsonRpc.MessagePackFormatter>

The very best performance comes from using the <xref:StreamJsonRpc.MessagePackFormatter> with the <xref:StreamJsonRpc.LengthHeaderMessageHandler>.
This combination is the fastest and produces the most compact serialized format.

The [MessagePack format][MessagePackFormat] is a fast, binary serialization format that resembles the
structure of JSON. It can be used as a substitute for JSON when both parties agree on the protocol for
significant wins in terms of performance and payload size.

Utilizing `MessagePack` for exchanging JSON-RPC messages is incredibly easy.
Check out the `BasicJsonRpc` method in our [MessagePackFormatterTests][MessagePackUsage] class.

#### When to use <xref:StreamJsonRpc.SystemTextJsonFormatter>

When the remote party does not support MessagePack but does support UTF-8 encoded JSON,
<xref:StreamJsonRpc.SystemTextJsonFormatter> offers the most performant choice available.

This formatter is compatible with remote systems that use @StreamJsonRpc.JsonMessageFormatter, provided they use the default UTF-8 encoding.
The remote party must also use the same message handler, such as <xref:StreamJsonRpc.HeaderDelimitedMessageHandler>.

#### When to use @StreamJsonRpc.JsonMessageFormatter

This formatter is the default for legacy reasons, and offers compatibility with data types that can only be serialized with Newtonsoft.Json.
It produces JSON text and allows configuring the text encoding, with UTF-8 being the default.

This formatter is compatible with remote systems that use <xref:StreamJsonRpc.SystemTextJsonFormatter> when using the default UTF-8 encoding.
The remote party must also use the same message handler, such as <xref:StreamJsonRpc.HeaderDelimitedMessageHandler>.

[MessagePackLibrary]: https://github.com/MessagePack-CSharp/MessagePack-CSharp
[MessagePackUsage]: https://github.com/microsoft/vs-streamjsonrpc/blob/main/test/StreamJsonRpc.Tests/MessagePackFormatterTests.cs
[MessagePackFormat]: https://msgpack.org/
[SystemTextJson]: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/overview
[spec]: https://www.jsonrpc.org/specification
