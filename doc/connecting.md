# Establishing a JSON-RPC connection

The JSON-RPC protocol communicates over an existing transport, such as a .NET `Stream` or `WebSocket`.

If using the `Stream` class, you may have a single full-duplex `Stream` (e.g. a `PipeStream` or `NetworkStream`)
or a pair of half-duplex `Stream` objects (e.g. STDIN and STDOUT streams). All APIs that accept both forms, but
this document will assume a single bidirectional `Stream` for brevity.

A JSON-RPC connection is created and managed via the `JsonRpc` class.
Certain decisions about the protocol details must be made up front while constructing the `JsonRpc` class.
These decisions are documented in our [extensibility](extensibility.md) document.
For this document, we will use the defaults for these up-front decisions.
Other configurations can be changed after instantiating the `JsonRpc` class. We will review these in this document.

## Client

To establish the JSON-RPC connection over a `Stream`, where you will only issue requests (not respond to them),
use the static `Attach` method:

```cs
JsonRpc rpc = JsonRpc.Attach(stream);
```

You can then proceed to send requests using the `rpc` variable. [Learn more about sending requests](sendrequest.md).

## Server (and possibly client also)

If you expect to respond to RPC requests, you can provide the target object that defines the methods that may be
invoked by the remote party:

```cs
var target = new LanguageServerTarget();
var rpc = JsonRpc.Attach(stream, target);
```

The `JsonRpc` object assigned to the `rpc` variable is now listening for requests on the stream and will invoke
methods on the `target` object as requested. You can also make requests with this `rpc` object just like the earlier example.
[Learn more about receiving requests](recvrequest.md).

In order to prevent closing underline connection in ApsNetCore pipeline, add awaiting of Completion to your middleware handler:

```cs
await rpc.Completion;
```

## Disconnecting

Once connected, a *listening* `JsonRpc` object will continue to operate till the connection is terminated, even if
the original creator drops the reference to that `JsonRpc` object. It is not subject to garbage collection because
the underlying transport has a reference to it for notifying of an incoming message.

[Learn more about disconnection](disconnecting.md).
