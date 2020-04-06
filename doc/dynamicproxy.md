# Dynamically generated proxies

When a JSON-RPC server's API is expressed as a .NET interface, StreamJsonRpc can dynamically create a proxy that implements that interface
to expose a strongly-typed client for your service. These proxies can be created using either the static `JsonRpc.Attach<T>(Stream)` method
or the instance `JsonRpc.Attach<T>()` method.

Generated proxies have the following behaviors:

1. They always pass arguments using positional arguments rather than named arguments.
1. Methods are sent as ordinary requests (not notifications).
1. Events on the interface are raised locally when a notification with the same name is received from the other party.
1. Implement `IDisposable` if created using the *static* `JsonRpc.Attach<T>` method, and terminate the connection when `Dispose()` is invoked.

A proxy can only be dynamically generated for an interface that meets these requirements:

1. Is public
1. No properties
1. No generic methods
1. All methods return `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or `IAsyncEnumerable<T>`.
   One exception to this is the RPC interface may derive from `IDisposable` (which declares the `void` returning `Dispose` method).
1. All events are typed with `EventHandler` or `EventHandler<T>`. The JSON-RPC contract for raising such events is that the request contain exactly one argument, which supplies the value for the `T` in `EventHandler<T>`.
1. Methods *may* accept a `CancellationToken` as the last parameter.

## Async methods and generated proxies

An interface used to generate a dynamic client proxy must return `Task` or `Task<T>` from all methods.
This allows the client proxy to be generated with asynchronous methods as appropriate for JSON-RPC (and IPC in general)
which is fundamentally asynchronous.

### Dispose patterns

The generated proxy *always* implements `IDisposable`, where `IDisposable.Dispose()` simply calls `JsonRpc.Dispose()`.
This interface method call does *not* send a "Dispose" RPC method call to the server.
The server should notice the dropped connection when the client was disposed and dispose the server object if necessary.

The RPC interface may derive from `IDisposable` and is encouraged to do so as it encourages folks who hold proxies to dispose of them and thereby close the JSON-RPC connection.

### Server-side concerns

On the server side, these same methods may be simple and naturally synchronous. Returning values from the server wrapped
in a `Task` may seem unnatural.
The server need not itself explicitly implement the interface -- it could implement the same method signatures as are
found on the interface except return `void` (or whatever your `T` is in your `Task<T>` method signature on the interface)
and it would be just fine. Of course implementing the interface may make it easier to maintain a consistent contract
between client and server.

### Client-side concerns

Sometimes a client may need to block its caller until a response to a JSON-RPC request comes back.
The dynamic proxy maintains the same async-only contract that is exposed by the `JsonRpc` class itself.
[Learn more about sending requests](sendrequest.md), particularly under the heading about async responses.
