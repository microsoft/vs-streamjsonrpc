# Passing `Stream`/`IDuplexPipe` around

JSON-RPC is great for invoking methods and passing regular data types as arguments.
When you want to pass binary data or stream a great deal of text without encoding as a very large JSON message,
StreamJsonRpc gives you an option to pass `Stream` or `IDuplexPipe` as an argument to an RPC method.

The content of the `Stream` or `IDuplexPipe` is transmitted out of band of the JSON-RPC channel so that no extra encoding is required. This out of band channel is provisioned from a [`MultiplexingStream`](https://github.com/AArnott/Nerdbank.Streams/blob/master/doc/MultiplexingStream.md) that can optionally be provided to the `JsonMessageFormatter` (or other formatters that support this feature). The `JsonRpc` connection itself is expected to be one of the channels in this `MultiplexingStream`.
This can be configured like this (creation of the `MultiplexingStream` is out of scope of this topic):

```cs
var formatter = new JsonMessageFormatter
{
    MultiplexingStream = mxstream,
};
var handler = new HeaderDelimitedMessageHandler(rpcChannel, formatter);
var jsonRpc = new JsonRpc(handler);
jsonRpc.StartListening();
```

You may now proceed to transmit OOB pipes/streams:

```cs
await jsonRpc.InvokeAsync("TakeLargeFileAsync", streamOrPipe);
```

The server may receive these with an RPC method signature such as:

```cs
public async Task TakeLargeFileAsync(Stream stream)
{
    // use the stream, then dispose it!
    stream.Dispose();
}

// OR

public async Task TakeLargeFileAsync(IDuplexPipe pipe)
{
    // Use the pipe then close it
    pipe.Input.Complete();
    pipe.Output.Complete();
}
```

## Rules

Passing out of band streams/pipes along JSON-RPC messages requires care be taken to avoid leaving
abandoned `MultiplexingStream` channels active and consuming resources in corner cases.
To faciliate this, the following rules apply:

1. The `IDuplexPipe` always originates on the client and is passed as an argument to the server.
   Servers are not allowed to return `IDuplexPipe` to clients because the server would have no feedback if the client dropped it, leaking resources.
1. The client can only send an `IDuplexPipe` in a request (that expects a response).
   Notifications would not provide the client with feedback that the server dropped it, leaking resources.
1. The client will immediately terminate the `IDuplexPipe` if the server returns ANY error in response to the request, since the server may not be aware of the `IDuplexPipe`.
1. The `IDuplexPipe` will NOT be terminated when a successful response is received from the server. Client and server are expected to negotiate the end of the `IDuplexPipe` themselves.

All rules apply equally to `Stream` and `IDuplexPipe`.

Closing an out of band channel should always be done on each end when done using it.
The way this is done varies between `Stream` and `IDuplexPipe` and is done as follows:

```cs
stream.Dispose();
```

or for pipes:

```cs
pipe.Input.Complete();
pipe.Output.Complete();
```

Pipes have to have their input and output completed individually.
An `IDuplexPipe` may have one direction of communication completed before the other direction,
which can be useful to communicate to the remote party that you are no longer reading or writing.
The channel is automatically shut down when both sides have completed reading and writing.

## `IDuplexPipe` or `Stream`?

The wire protocol for each of these is the same, so it is not necessary for the client and server to agree on which of these to use.
For example the server might define its method signature to accept a `Stream` while the client passes an `IDuplexPipe` instance to the server.

When your options are open, `IDuplexPipe` is the recommended type to use because:

1. it has lower overhead than a `Stream`
2. it can express when one side is done writing but may still be listening

But `Stream` may be the appropriate choice when:

1. You already have a `Stream` that you want to share. For example, you've opened a file and want to stream its contents or want to stream the stdout stream from another process.
