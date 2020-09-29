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

## Concurrency vs. message ordering

For maximum performance, requests can be handled concurrently.
But this brings up important questions when request order is important.

If your server state is mutable, it may be important for you to handle client requests in the order
the client originally sent them. This happens naturally if the client sends only one request at a time,
awaiting the result before making the next request.

If the client sends a stream of requests at a time, and expects you to process them strictly in-order, it becomes the RPC server's responsibility to maintain the order.
This ordering comes by setting the `JsonRpc.SynchronizationContext` property to a `SynchronizationContext` that preserves order of calls dispatched with `SynchronizationContext.Post`.

### `NonConcurrentSynchronizationContext`

The `Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext` supports this by scheduling incoming messages to the threadpool but disallowing concurrency.
When used in its "non-sticky" mode, the `NonConcurrentSynchronizationContext` invokes RPC server methods sequentially, but allows them to execute concurrently after their first yielding *await* (if any).
This ensures your server methods are invoked in-order and exclusively, but may opt into allowing another request to execute by yielding (e.g. `await Task.Yield();`), after which they may run concurrently with subsequent incoming requests.
When a server method is invoked, it may already be concurrent with a previously invoked server method that has already hit its first yielding *await*.

Suppose two requests are received to invoke RPC server methods `Op1Async()` and `Op2Async()`,
in that order but close together.
The `NonConcurrentSynchronizationContext` will ensure that `Op1Async()` is invoked first.
When `Op1Async` hits its first yielding `await`, `Op2Async` will be invoked.

What happens next depends on the `sticky` argument passed to the `NonConcurrentSynchronizationContext` constructor.

**When `sticky: true`**, anytime `Op1Async` and `Op2Async` hit a yielding await the default behavior will be
to resume on this same non-concurrent `SynchronizationContext` when whatever they are awaiting is done.
This means that although `Op1Async` and `Op2Async` may take turns executing as they each repeatedly yield until they each complete,
they will never actually run *concurrently* with each other.

**When `sticky: false`**, the first time an async method hits a yielding await it will resume on the threadpool
when whatever it is waiting on is done. Its continuation may now run concurrently with other code such as another RPC method.

When a yielding await uses `.ConfigureAwait(false)`, it takes that async method off the `SynchronizationContext`
at which point code execution will happen on the threadpool, concurrently with any other code.
The `sticky` value has no effect on concurrency of code that uses `.ConfigureAwait(false)`.

### Default ordering and concurrency behavior

**Important:** The default behavior changed in StreamJsonRpc v2.6.

In StreamJsonRpc v2.6 and later, `NonConcurrentSynchronizationContext` (in non-sticky mode) is the default behavior.
Prior to v2.6 the default behavior was no `SynchronizationContext`, which meant all RPC server invocations were immediately queued to the threadpool, allowing ordering to get scrambled.

Whether before or after StreamJsonRpc v2.6, either behavior can be achieved by setting (or clearing) the `JsonRpc.SynchronizationContext` property, as in this example:

```cs
var jsonRpc = new JsonRpc(stream);

// Get v2.6+ default behavior in versions before 2.6:
// ordering preserved at expense of no concurrency prior to first yielding await.
jsonRpc.SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false);

// Get pre-2.6 behavior in 2.6+ versions:
// no ordering => maximum throughput
jsonRpc.SynchronizationContext = null;

jsonRpc.AddLocalRpcTarget(this);
jsonRpc.StartListening();
```

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
