# Writing resilient code

## Thread-safety considerations

JSON-RPC is an inherently asynchronous protocol. Multiple concurrent requests are allowed.
StreamJsonRpc will dispatch method calls as the requests are processed, even while prior requests are still running.
`JsonRpc` always invokes local methods by posting to its `SynchronizationContext`.

The default `SynchronizationContext` schedules work to the thread pool, which allows your code
to respond to multiple requests concurrently. You should write your objects to be thread-safe to avoid
malfunctions when clients call multiple requests at once by following thread-safe patterns.

Making code thread-safe in the face of concurrency is the ideal solution, as it maximizes performance.
When your code is not thread-safe, you should work to avoid data corruption and other malfunctions by taking
either of the approaches described below to avoid concurrent execution.

### Throttling concurrency to 1

If your code has no particular thread affinity, but you want to prevent any concurrent execution,
you may enclose each invokable method inside a semaphore. Be sure to wait for the semaphore asynchronously
to avoid flooding your server's thread pool with waiters. Below is an example of a server target object that
defends against concurrency as a thread-safety mitigation:

```cs
class Server
{
    AsyncSemaphore semaphore = new AsyncSemaphore(1);

    public async Task DoSomething(CancellationToken cancellationToken)
    {
        using (await semaphore.EnterAsync(cancellationToken))
        {
            // Do really great stuff here.
        }
    }

    public async Task DoSomethingElse(CancellationToken cancellationToken)
    {
        using (await semaphore.EnterAsync(cancellationToken))
        {
            // Do other really great stuff here.
        }
    }
}
```

This approach guards not only against code executing concurrently on multiple threads, but also
prevents interleaving of async steps in your methods. For example, if `DoSomething` starts executing
then yields at an *await*, and before it returns a request comes in for `DoSomethingElse`, the semaphore
will block `DoSomethingElse` from executing until `DoSomething` actually finishes all its work.

**Important:** If any of your local methods respond by sending a request back to the client (a "callback"),
and the client responds by submitting another request, you may deadlock because the semaphore is already occupied
by the original request.

### Single-threaded processing of RPC requests

To execute all locally invoked RPC methods on a single, dedicated thread, you may replace
the default `SynchronizationContext` with a single-threaded one like this:

```cs
var jsonRpc = new JsonRpc(stream);

// Set up a sync context that will only execute code on one thread (this one).
var singleThreadedSyncContext = new SingleThreadedSynchronizationContext();
jsonRpc.SynchronizationContext = singleThreadedSyncContext;
var frame = new SingleThreadedSynchronizationContext.Frame();

// Arrange for the thread to just sit and wait for messages while the JSON-RPC connection lasts.
jsonRpc.Disconnected += (s, e) => frame.Continue = false;

// Initiate JSON-RPC message processing.
jsonRpc.StartListening();

// Start the "message pump" on this thread to execute code on behalf of JsonRpc.
// This call will block until the connection is dropped.
singleThreadedSyncContext.PushFrame(frame);
```

The [`SingleThreadedSynchronizationContext` class](https://github.com/Microsoft/vs-threading/blob/master/src/Microsoft.VisualStudio.Threading/SingleThreadedSynchronizationContext.cs) comes from the [Microsoft.VisualStudio.Threading NuGet package](https://nuget.org/packages/Microsoft.VisualStudio.Threading) (starting with the v16.0 version). But you may supply any `SynchronizationContext` you wish.

Note that while `JsonRpc` will always invoke local RPC methods using the `SynchronizationContext`,
if those methods are asynchronous and use `.ConfigureAwait(false)`, they may escape that `SynchronizationContext`
and execute partly on the threadpool, allowing concurrent execution.

This approach guards only against code executing concurrently on multiple threads. It does *not*
prevent interleaving of async steps in your methods. For example, if your `DoSomething` server method starts executing
then yields at an *await*, and before it returns a request comes in for `DoSomethingElse`, `DoSomethingElse` can
start executing on the dedicated thread immediately, and the original `DoSomething` method cannot resume until
`DoSomethingElse` yields the thread.

## When message order is important

If your server state is mutable, it may be important for your to respond to client requests in the order
the client originally sent them. This happens naturally if the client sends only one request at a time,
awaiting the result before making the next request.

If the client sends a stream of requests at a time, and expects you to process them strictly in-order,
you should use a custom `SynchronizationContext` that will only dispatch one call at a time, and use only
synchronous server methods (no *awaits*).

A possible improvement to handling message ordering is being [considered via the JSON-RPC request batching spec](https://github.com/Microsoft/vs-streamjsonrpc/issues/134), which would allow concurrency except where clients
explicitly constrain a list of requests for sequential execution.

## Fatal exceptions

StreamJsonRpc handles all exceptions thrown by server methods. Future requests from the client continue to be served.
This resembles an ordinary relationship between two objects in .NET.

In some cases you may consider an exception thrown from a server method to be fatal, and wish to terminate the connection
with the client. This can be accomplished by deriving from the `JsonRpc` class and overriding its `IsFatalException` method
such that it returns `true` for some subset of exceptions. When the method returns `true`, `JsonRpc` will terminate the connection.
The following is an example:

```cs
public class Server : BaseClass
{
    public void ThrowsException() => throw new Exception("Throwing an exception");
}

public class JsonRpcClosesStreamOnException : JsonRpc
{
    public JsonRpcClosesStreamOnException(Stream stream, object target = null) : base(stream, target)
    {
    }

    protected override bool IsFatalException(Exception ex)
    {
        return true;
    }
}

// Server code:
var serverRpc = new JsonRpcClosesStreamOnException(stream, new Server());
serverRpc.StartListening();
await serverRpc.Completion; // this will throw because the server killed the connection when `Server.ThrowsException` fails.
```

Note that `IsFatalException` is invoked within an exception filter, and thus will execute on top of the callstack
that is throwing the exception.

## Responding to unexpected disconnection

Inter-process communication can be a fickle thing. Network connections can get dropped, processes can crash, etc.
It's important that any JSON-RPC client or server is resilient in the face of a dropped connection.

If you need to take some action if the connection unexpectedly drops, you can add an event handler to the
`JsonRpc.Disconnected` event. This handler is provided the reason for the disconnection as well as any error details
that are available so they can be logged, and appropriate remediation taken.

### Clients

When a client detects an unexpected connection drop, it generally can take either of two actions:

1. Report the failure to the user.
1. Optimistically attempt to restart the server and/or reestablish the connection.

The former is generally straightforward and low risk.
The latter may be preferable in the connection is known to be unreliable.

Before the client decides to restart the server and retry any failed RPC calls, it should consider that the client's
last RPC call may be the reason the server crashed in the first place. Or perhaps the connection is so unreliable
that a stable connection cannot be maintained. In either of these cases, a client may be wise to not retry the
connection too quickly to mitigate the risk that the client or server experience a tight loop where it spins the CPU
continually retrying an operation that is doomed to fail.
A reasonable approach might be to employ an [exponential backoff](https://en.wikipedia.org/wiki/Exponential_backoff)
algorithm, or a maximum number of retries before finally reporting failure to the user and letting the user decide
whether to retry.

### Servers

Server methods continue to execute by default when connections are dropped. To abort server methods when the connection drops:

1. Each server method should accept a `CancellationToken` and periodically call `cancellationToken.ThrowIfCancellationRequested()`.
1. Set `JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed` to `true`.
