# Dynamically generated proxies

When a JSON-RPC server's API is expressed as a .NET interface, StreamJsonRpc can dynamically create a proxy that implements that interface
to expose a strongly-typed client for your service. These proxies can be created using either the static <xref:StreamJsonRpc.JsonRpc.Attach``1(System.IO.Stream)> method
or the instance <xref:StreamJsonRpc.JsonRpc.Attach``1> method.

Generated proxies have the following behaviors:

1. They default to passing arguments using positional arguments, with an option to send requests with named arguments instead.
1. Methods are sent as ordinary requests (not notifications).
1. Events on the interface are raised locally when a notification with the same name is received from the other party.
1. Implement <xref:System.IDisposable> if created using a *static* <xref:StreamJsonRpc.JsonRpc.Attach``1(System.IO.Stream)> method, and terminate the connection when <xref:System.IDisposable.Dispose> is invoked.

A proxy can only be dynamically generated for an interface that meets these requirements:

1. Is public
1. No properties
1. No generic methods
1. All methods return `void`, @System.Threading.Tasks.Task, @System.Threading.Tasks.Task`1, <xref:System.Threading.Tasks.ValueTask>, <xref:System.Threading.Tasks.ValueTask`1>, or <xref:System.Collections.Generic.IAsyncEnumerable`1>.
1. All events are typed with <xref:System.EventHandler> or <xref:System.EventHandler`1>. The JSON-RPC contract for raising such events is that the request contain exactly one argument, which supplies the value for the `T` in <xref:System.EventHandler`1>.
1. Methods *may* accept a @System.Threading.CancellationToken as the last parameter.

## Async methods and generated proxies

An interface used to generate a dynamic client proxy must return awaitable types from all methods.
This allows the client proxy to be generated with asynchronous methods as appropriate for JSON-RPC (and IPC in general)
which is fundamentally asynchronous.

A method's return type may also be `void`, in which case the method sends a notification to the RPC server and does not wait for a response.

### Dispose patterns

The generated proxy *always* implements <xref:System.IDisposable>, where <xref:System.IDisposable.Dispose?displayProperty=nameWithType> simply calls <xref:StreamJsonRpc.JsonRpc.Dispose?displayProperty=nameWithType>.
This interface method call does *not* send a "Dispose" RPC method call to the server.
The server should notice the dropped connection when the client was disposed and dispose the server object if necessary.

The RPC interface may derive from <xref:System.IDisposable> and is encouraged to do so as it encourages folks who hold proxies to dispose of them and thereby close the JSON-RPC connection.

### Server-side concerns

On the server side, these same methods may be simple and naturally synchronous. Returning values from the server wrapped
in a @System.Threading.Tasks.Task may seem unnatural.
The server need not itself explicitly implement the interface -- it could implement the same method signatures as are
found on the interface except return `void` (or whatever your `T` is in your @System.Threading.Tasks.Task`1 method signature on the interface)
and it would be just fine. Of course implementing the interface may make it easier to maintain a consistent contract
between client and server.

### Client-side concerns

Sometimes a client may need to block its caller until a response to a JSON-RPC request comes back.
The dynamic proxy maintains the same async-only contract that is exposed by the @StreamJsonRpc.JsonRpc class itself.
[Learn more about sending requests](sendrequest.md), particularly under the heading about async responses.

## AssemblyLoadContext considerations

When in a .NET process with multiple <xref:System.Runtime.Loader.AssemblyLoadContext> (ALC) instances, you should consider whether StreamJsonRpc is loaded in an ALC that can load all the types required by the proxy interface.

By default, StreamJsonRpc will generate dynamic proxies in the ALC that the (first) interface requested for the proxy is loaded within.
This is usually the right choice because the interface should be in an ALC that can resolve all the interface's type references.
When you request a proxy that implements *multiple* interfaces, and if those interfaces are loaded in different ALCs, you *may* need to control which ALC the proxy is generated in.
The need to control this may manifest as an <xref:System.MissingMethodException> or <xref:System.InvalidCastException> due to types loading into multiple ALC instances.

In such cases, you may control the ALC used to generate the proxy by surrounding your proxy request with a call to <xref:System.Runtime.Loader.AssemblyLoadContext.EnterContextualReflection*> (and disposal of its result).

For example, you might use the following code when StreamJsonRpc is loaded into a different ALC from your own code:

```cs
// Whatever ALC can resolve *all* type references in *all* proxy interfaces.
AssemblyLoadContext alc = AssemblyLoadContext.GetLoadContext(MethodBase.GetCurrentMethod()!.DeclaringType!.Assembly);
IFoo proxy;
using (AssemblyLoadContext.EnterContextualReflection(alc))
{
    proxy = (IFoo)jsonRpc.Attach([typeof(IFoo), typeof(IFoo2)]);
}
```

This initializes the `proxy` local variable with a proxy that will be able to load all types that your own <xref:System.Runtime.Loader.AssemblyLoadContext> can load.
