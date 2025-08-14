# Strongly typed proxies

A client may of course issue loosely typed RPC requests, like this:

[!code-csharp[](../../samples/Proxies.cs#WithoutProxies)]

But it can provide a superior experience to use strongly-typed proxies.

When a JSON-RPC server's API is expressed as a .NET interface, StreamJsonRpc can create a proxy that implements that interface
to expose a strongly-typed client for your service.
These proxies can be created using either the static <xref:StreamJsonRpc.JsonRpc.Attach``1(System.IO.Stream)> method or the instance <xref:StreamJsonRpc.JsonRpc.Attach``1> method (or their overloads).

This changes the above example to something like this:

[!code-csharp[](../../samples/Proxies.cs#WithProxies)]

## Proxy traits

Generated proxies have the following traits:

1. They default to passing arguments using positional arguments, with [an option](xref:StreamJsonRpc.JsonRpcProxyOptions.ServerRequiresNamedArguments) to send requests with named arguments instead.
1. Methods are sent as ordinary requests (not notifications) when they return some form of @System.Threading.Tasks.Task or @System.Threading.Tasks.ValueTask.
   They are sent as notifications if they return `void`.
1. Events on the interface are raised locally when a notification with the same name is received from the other party.
1. They implement <xref:StreamJsonRpc.IJsonRpcClientProxy>, allowing access back to the <xref:StreamJsonRpc.JsonRpc> instance that created them.
1. They implement <xref:System.IDisposable>.
   *If* the proxy is created using a *static* <xref:StreamJsonRpc.JsonRpc.Attach``1(System.IO.Stream)> overload, disposal of the proxy is forwarded to <xref:StreamJsonRpc.JsonRpc.Dispose>.

## RPC interfaces

A proxy can only be generated for an interface that meets these requirements:

1. No properties.
1. No generic methods.
1. All methods return `void`, @System.Threading.Tasks.Task, @System.Threading.Tasks.Task`1, <xref:System.Threading.Tasks.ValueTask>, <xref:System.Threading.Tasks.ValueTask`1>, or <xref:System.Collections.Generic.IAsyncEnumerable`1>.
1. All events are typed with <xref:System.EventHandler> or <xref:System.EventHandler`1>. The JSON-RPC contract for raising such events is that the request contain exactly one argument, which supplies the value for the `T` in <xref:System.EventHandler`1>.
1. Methods *may* accept a @System.Threading.CancellationToken as the last parameter.

The RPC interface may derive from <xref:System.IDisposable> and is encouraged to do so as it encourages folks who hold proxies to dispose of them and thereby close the JSON-RPC connection.

Applying the <xref:StreamJsonRpc.JsonRpcContractAttribute> to all RPC interfaces is strongly encouraged, as it has two significant benefits:

1. Enables analyzers to report warnings due to violations of the above rules at compile-time instead of throwing at runtime.
1. Enables source generated proxies to be used at runtime instead of proxies created dynamically at runtime, leading to faster startup and [NativeAOT](nativeAOT.md) compatibility.

### Server-side concerns

On the server side, these same methods may be simple and naturally synchronous.
Returning values from the server wrapped in a @System.Threading.Tasks.Task may seem unnatural.
The server need not itself explicitly implement the interface -- it could implement the same method signatures as are found on the interface except return `void` (or whatever your `T` is in your @System.Threading.Tasks.Task`1 method signature on the interface) and it would be just fine.

[!code-csharp[](../../samples/Proxies.cs#ServerWithoutInterface)]

Of course implementing the interface may make it easier to maintain a consistent contract between client and server.
In which case, you can declare your server methods to also return @System.Threading.Tasks.Task`1 values and implement synchronous methods to return values using <xref:System.Threading.Tasks.Task.FromResult*?displayProperty=nameWithType>.

[!code-csharp[](../../samples/Proxies.cs#ServerWithInterface)]

### Client-side concerns

Sometimes a client may need to block its caller until a response to a JSON-RPC request comes back.
The proxy maintains the same async-only contract that is exposed by the <xref:StreamJsonRpc.JsonRpc> class itself.
[Learn more about sending requests](sendrequest.md), particularly under the heading about async responses.

## Dynamic proxies

The following concerns are related specifically to dynamically generated proxies and do not apply to source generated proxies.

### AssemblyLoadContext considerations

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
