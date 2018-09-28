# Dynamically generated proxies

When a JSON-RPC server's API is expressed as a .NET interface, StreamJsonRpc can dynamically create a proxy that implements that interface
to expose a strongly-typed client for your service. These proxies can be created using either the static `JsonRpc.Attach<T>(Stream)` method
or the instance `JsonRpc.Attach<T>()` method.

Generated proxies have the following behaviors:

1. They always pass arguments using positional arguments rather than named arguments.
1. Methods are sent as ordinary requests (not notifications).
1. Events on the interface are raised locally when a notification with the same name is received from the other party.
1. Implement `IDisposable` if created using the *static* `JsonRpc.Attach<T>` method, and terminate the connection when `Dispose()` is invoked.

A proxy can only be dynamically generated for an interface that meets this requirements:

1. No properties
1. No generic methods
1. All methods return `Task` or `Task<T>`
1. All events are typed with `EventHandler` or `EventHandler<T>`
1. Methods *may* accept a `CancellationToken` as the last parameter.
