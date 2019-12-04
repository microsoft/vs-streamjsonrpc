# Sending a JSON-RPC request

In this document we discuss the several ways you can send RPC requests to the remote party.

Before sending any request, you should have already [established a connection](connecting.md).
In each code sample below, we assume your `JsonRpc` instance is stored in a local variable called `rpc`.

## Requests and notifications

In the JSON-RPC protocol, an RPC request may indicate whether the client expect a response from the server.
A request that does not expect a response is called a notification. Notifications provide _no_ feedback to
the client as to whether the server honored the request (vs. dropping it), successfully carried it out
(instead of throwing an exception), or any result value that came from it. The client should _not_ use a
notification simply because there is no return value from a method. A request-response pattern is appropriate
even for void-returning methods so that the client will know if the server failed to invoke the method.

The `JsonRpc` class offers methods that begin with `Invoke` or `Notify` to identify these two kinds of
requests, where `Invoke*` methods will solicit a response from the server and `Notify*` methods will not.
All these methods are async. `Invoke*` methods return `Task` or `Task<T>` that will complete when the client
receives the response from the server. The `Notify*` methods return a `Task` that completes when the notification has been transmitted.

Notifications can be sent at any time. Ordinary requests can only be sent after the `JsonRpc` instance
has started listening to messages, so that the response message will be observed.
Listening is started either by calling the static `Attach` method or via a call to `StartListening()` instance method.

## Argument arrays vs. an argument object

The JSON-RPC protocol passes arguments from client to server using either an array
or as a single JSON object with a property for each parameter on the target method.
Essentially, this leads to argument to parameter matching by position or by name.

Most JSON-RPC servers expect an array.
JSON-RPC servers that operate in a Javascript environment in fact cannot do parameter name matching at all.

StreamJsonRpc supports passing arguments both as a parameter object and in an array.

## Cancellation

Although request cancellation isn't provided for in the JSON-RPC spec,
the Language Server Protocol Spec defines [a general purpose cancellation protocol for JSON-RPC](https://github.com/Microsoft/language-server-protocol/blob/9ada034c14d63b610772100f07885af89c4e4f1a/versions/protocol-2-x.md#cancelRequest),
which StreamJsonRpc supports. Notifications cannot be canceled.

A client can cancel a request by canceling a `CancellationToken` supplied when originally invoking the request.
Be sure to call the `JsonRpc.Invoke*` method and overload that explicitly accepts `CancellationToken`
(e.g. `InvokeWithCancellationAsync` or `InvokeWithParameterObjectAsync`) rather than supplying the token
in the params array that must be serializable.

When canceled, this `CancellationToken` may abort the transmission of the original request if it has not yet been transmitted. Or if the request has already been transmitted, a notification is sent to the server
advising the server that the request has been canceled from the client, giving the server the opportunity
to abort the invocation and return a canceled result.

After a `CancellationToken` is canceled, the `Task` originally returned from the `JsonRpc.Invoke*` method
will reflect cancellation only after the request's transmission is avoided or the server responds with a
cancellation result. Otherwise, the `Task` will still complete or fault based on the server's ordinary
response to the original request.

## Responses come back asynchronously

As JSON-RPC is fundamentally an async protocol, and typically used to communicate between processes or machines,
`JsonRpc` exposes only async methods for sending requests and awaiting responses.

In fact, supporting non-Task returning methods would be potentially very problematic because that would mean StreamJsonRpc is responsible to artificially block the calling thread until the result came back. But that can quickly lead to hangs if, for example, the calling thread is your app's UI thread. It can deadlock if the server needs to call back to your client before returning a response, and your client can't respond because its UI thread is hung.

When you have an unavoidable requirement to synchronously block while waiting for *any* async code to complete (whether JSON-RPC related or otherwise),
we recommend use of the [JoinableTaskFactory](https://aka.ms/vsthreading) as found in the [Microsoft.VisualStudio.Threading](https://www.nuget.org/packages/Microsoft.VisualStudio.threading) library. That can block the UI thread (or any other thread) while executing async operations. In the case of StreamJsonRpc, that means the client might have something like this:

```cs
interface IJsonRpcService
{
    Task SomeOperationAsync();
}

var client = JsonRpc.Attach<IJsonRpcService>(pipe);

// Call the operation, blocking the thread till the operation is done:
joinableTaskFactory.Run(async delegate
{
    await client.SomeOperationAsync();
});
```

The upside of taking this approach is that you're writing new code that is async, which can be called asynchronously in all your *new* scenarios, all the while still satisfying your legacy synchronous requirements. It's a "bridge" into the new async world that can actually work in practice. :)

## Invoking methods (and requesting responses) with weak typing

### Invoking using positional arguments (array)

To invoke a remote method named "foo" which takes an `int` and a `string` parameter, and returns an `int`:

```cs
int myResult = await rpc.InvokeAsync<int>("foo", 18, "brown");
```

The parameters will be passed remotely as an array of objects.

### Invoking using named arguments (object)

Instead of using positional arguments as shown in the example above, we might instead use named arguments
by using `InvokeWithParameterObjectAsync`.
The arguments are provided as values of properties named after the parameters in the invoked method.
In C# this can be expressed using anonymous types, as shown here:

```cs
int myResult = await rpc.InvokeWithParameterObjectAsync<int>("baz", new { age = 18, hair = "brown" });
```

## Sending a notification with weak typing

### Use positional arguments (array)

To trigger a remote method named "foo" which takes a `string` and an `int` parameter,
without regard to its result:

```cs
await rpc.NotifyAsync("foo", "arg1", 5);
```

### Use named arguments (object)

To trigger a remote method named "foo" which takes parameters `hair` and `age`,
without regard to its result:

```cs
await rpc.NotifyWithParameterObjectAsync("bar", new { age = 5, hair = "brown" });
```

## Strongly-typed requests

Suppose you have an interface that defines the JSON-RPC server's API:

```cs
interface IServer
{
    Task AddIngredientAsync(string name, int amount);
    Task<string[]> GetAllIngredientsAsync();
}
```

You can utilize this interface for strong-typed access from the client:

```cs
IServer server = rpc.Attach<IServer>();
await server.AddIngredientAsync("eggs", 2); // equivalent to rpc.InvokeAsync("AddIngredientAsync", "eggs", 2);
```

In the above example, the `JsonRpc.Attach<T>()` method dynamically generated a proxy that
implements `IServer` in terms of `JsonRpc` calls, and returned an instance of that object.

A *static* `T JsonRpc.Attach<T>(Stream)`  method also exists which streamlines the use case of
connecting and obtaining a dynamic proxy:

```cs
IServer server = JsonRpc.Attach<IServer>(stream);
await server.AddIngredientAsync("eggs", 2);
```

With this streamlined flow, you never get access to a `JsonRpc` instance.
Terminating the connection is done by calling the proxy's `IDisposable.Dispose()` method:

```cs
((IDisposable)server).Dispose(); // terminate the connection
```

[Learn more about dynamically generated proxies](dynamicproxy.md).

## Exception handling

RPC methods may throw exceptions.
The RPC client should be prepared to handle these exceptions.

[Learn more about throwing and handling exceptions](exceptions.md).
