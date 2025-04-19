# `IProgress<T>`

Support for `IProgress<T>` allows a JSON-RPC server to report progress to the client periodically so that the client can start processing those
results without having to wait for all results to be retrieved before responding.

## Client side

With this support client can include an object that implements `IProgress<T>` on the invocation parameters.
The Report method on that instance should have the processing that the client will do with the results reported by the server.

Since `IProgress<T>` is not directly serializable to JSON, a JSON token that tracks that `IProgress<T>` instance will be written in its place
in the outgoing message.

The request sent to the server will look like this:

```json
{
  "jsonrpc": "2.0",
  "id": 153,
  "method": "someMethodName",
  "params": {
    "units": 5,
    "progress": "some-JSON-token"
  }
}
```

The progress argument may have any name or position. Its value will be determined by the <xref:StreamJsonRpc.IJsonRpcMessageFormatter> and may be any valid JSON token.
A value of null is specially recognized as an indication that the client does not want progress updates from the server.

Reports from the server will come in the form of special notifications to the `$/progress` method, which will include the token that replaced the object
implementing `IProgress<T>` and the values to report. Whenever the client receives this kind of notification, it will invoke the `IProgress<T>` instance
associated with the original request the client placed.

This reporting will continue until the client receives the actual JSON-RPC response message for the request.
Any `$/progress` notifications from the server with the matching JSON progress token will be discarded after that point.

### Timing considerations

- All reports made by the server are guaranteed to be invoked on the client's `IProgress<T>.Report` method in the same order as the server made them. Note that the commonly used `Progress<T>` class schedules invocations of the delegate you provide on the thread pool unless you create it with a <xref:System.Threading.SynchronizationContext> (e.g. create it on the main thread of a GUI application). Since the thread pool does not guarantee order of execution, this means progress reports may appear to be out of order unless you use an order-preserving <xref:System.Threading.SynchronizationContext> or another `IProgress<T>` implementation.
- All invocations of `IProgress<T>.Report` will have completed *before* the outbound RPC call that provided the `IProgress<T>` argument is considered completed.
- A client's implementation of `IProgress<T>.Report` should avoid blocking on other threads or completion of other RPC work, as this may introduce a deadlock with @StreamJsonRpc.JsonRpc since it cannot make further progress reading RPC messages until this method returns.
- If the `IProgress<T>.Report` has significant work to do, consider using an `IProgress<T>` implementation whose `Report` method simply schedules work to happen and returns quickly.
  Use of [`ProgressWithCompletion<T>`](https://learn.microsoft.com/dotnet/api/microsoft.visualstudio.threading.progresswithcompletion-1) is recommended because it schedules the handling for later and provides a [`WaitAsync`](https://learn.microsoft.com/dotnet/api/microsoft.visualstudio.threading.progresswithcompletion-1.waitasync) method that can be used to ensure the client has fully processed progress reports before continuing. For example:

  ```cs
  ProgressWithCompletion<Update> progress = new(u => SlowHandlerAsync(u));
  SomeResult result = await jsonRpcProxy.DoSomethingBigAsync(progress, cancellationToken);
  await progress.WaitAsync(cancellationToken); // wait for stragglers before continuing.
  ```

## Server side

When the method called on the server takes an `IProgress<T>` parameter, the JSON token that supplies the argument for that parameter will be considered
the progress token used to send progress via `$/progress` back to the client. The server method will be invoked with an instance of `IProgress<T>`
which may be used as normal until the server method completes, after which the `IProgress<T>` becomes inert.

This new `IProgress<T>` instance will send a special kind of notification ($/progress) whenever results are reported on the server method, using
the token given by the client and the reported values as parameters.

The progress notification will look like this:

```json
{
  "jsonrpc": "2.0",
  "method": "$/progress",
  "params": {
    "token": "some-JSON-token",
    "value": { "some": "status-token" }
  }
}
```

## Links

[Spec proposal](https://github.com/microsoft/vs-streamjsonrpc/issues/139)
[Issue documenting the protocol](https://github.com/microsoft/language-server-protocol/issues/786)
