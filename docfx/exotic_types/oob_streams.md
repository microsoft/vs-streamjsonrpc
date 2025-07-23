# Passing @System.IO.Stream/@System.IO.Pipelines.IDuplexPipe around

JSON-RPC is great for invoking methods and passing regular data types as arguments.
When you want to pass binary data or stream a great deal of text without encoding as a very large JSON message,
StreamJsonRpc gives you an option to pass @System.IO.Stream, @System.IO.Pipelines.IDuplexPipe, `PipeReader` or `PipeWriter` as an argument or as a return type for an RPC method.

The content of the @System.IO.Stream or @System.IO.Pipelines.IDuplexPipe is transmitted out of band of the JSON-RPC channel so that no extra encoding is required. This out of band channel is provisioned from a [`MultiplexingStream`](https://dotnet.github.io/Nerdbank.Streams/docs/MultiplexingStream.html) that can optionally be provided to the @StreamJsonRpc.JsonMessageFormatter (or other formatters that support this feature). The @StreamJsonRpc.JsonRpc connection itself is expected to be one of the channels in this `MultiplexingStream`.
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

The server may also reply with stream or pipe:

```cs
public Stream GetFile(string path) {
    // Validate that the client should be granted access to the requested file.
    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
}
```

## Rules

Passing out of band streams/pipes along JSON-RPC messages requires care be taken to avoid leaving
abandoned `MultiplexingStream` channels active and consuming resources in corner cases.
To facilitate this, the following rules apply:

1. The client can only send an @System.IO.Pipelines.IDuplexPipe in a request (that expects a response).
   Notifications would not provide the client with feedback that the server dropped it, leaking resources.
1. The client will immediately terminate the @System.IO.Pipelines.IDuplexPipe if the server returns ANY error in response to the request, since the server may not be aware of the @System.IO.Pipelines.IDuplexPipe.
1. The @System.IO.Pipelines.IDuplexPipe will NOT be terminated when a successful response is received from the server. Client and server are expected to negotiate the end of the @System.IO.Pipelines.IDuplexPipe themselves.

All rules apply equally to @System.IO.Stream and @System.IO.Pipelines.IDuplexPipe.

Closing an out of band channel should always be done on each end that can transmit over the stream, when done writing (and reading, where applicable).
The way this is done varies between @System.IO.Stream and @System.IO.Pipelines.IDuplexPipe and is done as follows:

```cs
// If the stream can be used to transmit data, we need to dispose the stream when we're done using it.
// Do NOT dispose of the stream if it strictly receives data from the remote party
// since the stream will be disposed automatically when the remote party indicates they are done transmitting.
stream.Dispose();
```

or for pipes:

```cs
// We're done receiving. We don't expect any more data or don't care to read it if there is any.
pipe.Input.Complete();

// We're done transmitting to the other side.
pipe.Output.Complete();
```

Pipes have to have their input and output completed individually.
An @System.IO.Pipelines.IDuplexPipe may have one direction of communication completed before the other direction,
which can be useful to communicate to the remote party that you are no longer reading or writing.
The channel is automatically shut down when both sides have completed reading and writing.

When you have a @System.IO.Stream that will only be used to receive data from the remote party,
it is not safe to assume that all data has been received when the RPC call is complete
since the data comes over another channel at its own pace.
If you need to know when the @System.IO.Stream has received all data you have two options:

1. When *you* are the one reading from the @System.IO.Stream directly, note when a `ReadAsync` call returns 0 bytes.
   This indicates the remote party is done transmitting.
1. When the @System.IO.Stream is an argument you are passing to the RPC server, and you are *not* reading the stream directly
   (e.g. it's a `FileStream` and the remote party is writing the file for you),
   you can first wrap the @System.IO.Stream in a [`MonitoringStream`](https://dotnet.github.io/Nerdbank.Streams/docs/MonitoringStream.html)
   and pass that wrapper in as your @System.IO.Stream argument.
   This gives you an option to observe when the @System.IO.Stream is disposed. For example:

   ```cs
   var fs = new MonitoringStream(new FileStream("somefile.txt", FileMode.Create, FileAccess.Write));
   var disposed = new AsyncManualResetEvent();
   fs.Disposed += (s, e) => disposed.Set();
   try
   {
      await jsonRpc.InvokeAsync("GetFileContent", new object[] { monitoredStream }, cancellationToken);
   }
   catch (Exception ex) when (!(ex is RemoteInvocationException))
   {
      // The only failure case where the stream will be closed automatically is
      // if it came in as an error response from the server.
      fs.Dispose();
      throw;
   }

   await disposed.WaitAsync(cancellationToken);
   ```

## @System.IO.Pipelines.IDuplexPipe or @System.IO.Stream?

The wire protocol for each of these is the same, so it is not necessary for the client and server to agree on which of these to use.
For example the server might define its method signature to accept a @System.IO.Stream while the client passes an @System.IO.Pipelines.IDuplexPipe instance to the server.

When your options are open, @System.IO.Pipelines.IDuplexPipe is the recommended type to use because:

1. it has lower overhead than a @System.IO.Stream
2. it can express when one side is done writing but may still be listening

`PipeReader` and `PipeWriter` are one-way components of an @System.IO.Pipelines.IDuplexPipe and when used with StreamJsonRpc are equivalent in terms of efficiency, but convey in the API that only one direction of communication is supported.

But @System.IO.Stream may be the appropriate choice when:

1. You already have a @System.IO.Stream that you want to share. For example, you've opened a file and want to stream its contents or want to stream the stdout stream from another process.
