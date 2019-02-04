# Establishing a JSON-RPC connection

The JSON-RPC protocol communicates over an existing transport, such as a .NET `Stream` or `WebSocket`.

If using the `Stream` class, you may have a single full-duplex `Stream` (e.g. a `PipeStream` or `NetworkStream`)
or a pair of half-duplex `Stream` objects (e.g. STDIN and STDOUT streams). All APIs that accept both forms, but
this document will assume a single bidirectional `Stream` for brevity.

A JSON-RPC connection is created and managed via the `JsonRpc` class.

## Connecting

Certain decisions about the protocol details must be made up front while constructing the `JsonRpc` class.
This library includes several built-in protocol variants and options, and you can add your own. This is all documented in our [extensibility](extensibility.md) document.

In the remaining samples in this section we use the convenient static `Attach` method which
instantiates the class with the default settings and begins listening for messages immediately. Samples for changing aspects of the protocol are in [a section below](#Configuring).

### Client

To establish the JSON-RPC connection over a `Stream`, where you will only issue requests (not respond to them),
use the static `Attach` method:

```cs
JsonRpc rpc = JsonRpc.Attach(stream);
```

You can then proceed to send requests using the `rpc` variable. [Learn more about sending requests](sendrequest.md).

### Server (and possibly client also)

If you expect to respond to RPC requests, you can provide the target object that defines the methods that may be
invoked by the remote party:

```cs
var target = new LanguageServerTarget();
var rpc = JsonRpc.Attach(stream, target);
```

The `JsonRpc` object assigned to the `rpc` variable is now listening for requests on the stream and will invoke
methods on the `target` object as requested. You can also make requests with this `rpc` object just like the earlier example.
[Learn more about receiving requests](recvrequest.md).

For servers that should wait for incoming RPC requests until the client disconnects, utilize the `JsonRpc.Completion` property
which returns a `Task` that completes when the connection drops. By awaiting this after attaching `JsonRpc` to the stream,
and before disposing the stream, you can hold the connection open as long as the client maintains it:

```cs
await rpc.Completion;
```

## Configuring/customizing the protocol <a name="Configuring"></a>

To alter the protocol in any way from the defaults, use the `JsonRpc` constructor directly, instead of using the static `Attach` method.
This gives you a chance to provide your own `IJsonRpcMessageHandler`, set text encoding, etc. before sending or receiving any messages.
Remember after configuring your instance to start listening by calling the `JsonRpc.StartListening()` method. This step is not necessary when using the static `Attach` method because it calls `StartListening` for you.

To make it easier for the receiver to know when it has received a complete JSON-RPC message,
we transmit the length in bytes of a message before the message itself.
We also transmit the text encoding used in the message.
The default is to use HTTP-like headers to do so.

    Content-Length: 38
    Content-Type: application/vscode-jsonrpc;charset=utf-8

    {"jsonrpc":"2.0","id":1,"result":"hi"}

When receiving a message, UTF-8 is assumed if not explicitly specified.
When transmitting a message with UTF-8 encoding, the `Content-Type` header is omitted for brevity.

Suppose that instead of introducing each JSON-RPC message with an HTTP-like header to disclose the size of the message, you want to establish a high performance connection that simply transmits a 4-byte big endian integer set to the length before each message.

You can do that like so:

```cs
Stream send, recv;
var formatter = new JsonMessageFormatter(Encoding.UTF8);
var handler = new LengthHeaderMessageHandler(send, recv, formatter);
var jsonRpc = new JsonRpc(handler);
jsonRpc.StartListening();
```

You could go further in achieving a high performance connection by replacing the `JsonMessageFormatter` in the above code with a binary formatter. [Learn more about alternative formatters and handlers](extensibility.md).

It is important that both sides of a connection agree on the protocol settings.

## Disconnecting

Once connected, a *listening* `JsonRpc` object will continue to operate till the connection is terminated, even if
the original creator drops the reference to that `JsonRpc` object. It is not subject to garbage collection because
the underlying transport has a reference to it for notifying of an incoming message.

[Learn more about disconnection](disconnecting.md).
