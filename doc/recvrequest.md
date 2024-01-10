# Receiving a JSON-RPC request

In this document we discuss the several ways you can receive and respond to RPC requests from the remote party.

Before receiving any request, you should have already [established a connection](connecting.md).
In each code sample below, we assume your `JsonRpc` instance is stored in a local variable called `rpc`.

When a request is received, `JsonRpc` matches it to a server method that was previously registered with
a matching name and list of parameters. If no matching server method can be found the request is dropped,
and an error is returned to the client if the client requested a response.

To help prevent accidental dropping of RPC requests that are received before matching server methods are registered,
`JsonRpc` requires all server methods to be registered *before* any incoming messages are processed.
Message processing begins automatically when the static `JsonRpc.Attach` method is used.
If `JsonRpc` was created using its constructor, message processing does not begin until the `JsonRpc.StartListening()`
method is invoked.

RPC server methods may:

1. Accept a `CancellationToken` as a last parameter, which signals that the client requested cancellation.
1. Define parameters with default values (e.g. `void Foo(string v, bool end = false)`), which makes them optional.
1. Be implemented synchronously, or be async by returning a `Task` or `Task<T>`.
1. Have multiple overloads, but [with caveats](#OverloadCaveats).

**Important notes**:

1. When an RPC-invoked server method throws an exception, StreamJsonRpc will handle the exception and (when applicable) send an error response to the client with a description of the failure. [Learn more about this and how to customize error handling behavior](exceptions.md).
1. RPC servers may be invoked multiple times concurrently to keep up with incoming client requests. By default, requests are dispatched one at a time, in order. When an async RPC method yields (i.e. returns a Task, whether complete or incomplete) the next request can be dispatched. Therefore concurrency of request handling can only happen when RPC server methods are implemented asynchronously. To achieve concurrent dispatch (where order is _not_ preserved) even when RPC server methods are implemented synchronously, set [`JsonRpc.SynchronizationContext`](https://docs.microsoft.com/en-us/dotnet/api/streamjsonrpc.jsonrpc.synchronizationcontext) to `null` before calling [`JsonRpc.StartListening`](https://docs.microsoft.com/en-us/dotnet/api/streamjsonrpc.jsonrpc.startlistening).

[Learn more about writing resilient servers](resiliency.md).

## Registering all public methods/events on an object

The simplest way to register methods for RPC is to collect them as public methods on a single class:

```cs
class Server
{
    public static int SumOf(int a, int b) => a + b;
    public int Difference(int a, int b) => a - b;
    public Task<string> ReadFileAsync(string path) => File.ReadAllTextAsync(path);

    private void NotReachable()
    {
        // This method cannot be invoked via RPC because it is not public
        // when using StreamJsonRpc 2.0 or later. In 1.x, the default allowed private methods to be invoked remotely.
    }
}
```

You can now pass an instance of this class to `JsonRpc` to register all public methods at once,
and start listening for requests:

```cs
JsonRpc rpc = JsonRpc.Attach(stream, new Server());
```

StreamJsonRpc automatically creates an alias for async methods that omit the `Async` suffix.
Given the example above, the JSON-RPC server will now respond to `SumOf`, `Difference`, `ReadFileAsync`,
and `ReadFile` methods.

### Invocation of non-public methods

The `JsonRpcTargetOptions.AllowNonPublicInvocation` property controls whether a target object's non-public methods
are invokable by a JSON-RPC client. In StreamJsonRpc 1.x this property defaults to `true`, but defaults to `false`
as of 2.0 for better security by default.

To adjust the value to a non-default setting, add the target object using the `JsonRpc.AddLocalRpcTarget` method
instead of using the `JsonRpc.Attach` method of the `JsonRpc` constructor that accepts a target object as an argument.
For example:

```cs
JsonRpc rpc = new JsonRpc(stream);
rpc.AddLocalRpcTarget(
    new Server(),
    new JsonRpcTargetOptions
    {
        AllowNonPublicInvocation = false,
    });
rpc.StartListening();
```

### Blocking invocation of specific methods

When a method on a target object would normally be exposed to the RPC client (either because it is `public` or because `JsonRpcTargetOptions.AllowNonPublicInvocation` has been set to `true`) and that method should *not* be exposed to RPC, apply the `[JsonRpcIgnore]` attribute to the method.

### Server events

When a server object defines public events, those events become notifications for the client.
Consider this server class:

```cs
class Server
{
    public event EventHandler<FileChangedEventArgs> FileChanged;

    internal void OnFileChanged(FileChangedEventArgs args) => this.FileChanged?.Invoke(this, args);
}
```

When you raise the `FileChanged` event on the server, `JsonRpc` will relay that as a notification message
back to the client. [Learn more about dynamic proxies on the client](dynamicproxy.md) and how they can
manifest these notifications as natural .NET events on the client.

You can customize the method names used in the event notification by adding the server target object
with a `JsonRpcTargetOptions` with a custom function set to its EventNameTransform property.

You can stop `JsonRpc` from sending notifications for events on the server object by adding the target object
with a `new JsonRpcTargetOptions { NotifyClientOfEvents = false }` argument (the default is `true`).
You may want to turn off the event functionality if your target object is reused from another class
and has events that shouldn't be exposed to RPC.

### Special method names

When the method naming convention you want to expose via RPC differs from the .NET naming convention you want
to use locally, you can apply a transform. For example, in the `Server` class above, we can expose the methods
using camelCase instead of PascalCase by applying a transform like this:

```cs
JsonRpc rpc = new JsonRpc(stream);
rpc.AddLocalRpcTarget(
    new Server(),
    new JsonRpcTargetOptions
    {
        MethodNameTransform = CommonMethodNameTransforms.CamelCase,
    });
rpc.StartListening();
```

A couple of these transforms come built in, including `CamelCase` and `Prepend`. You can write your own as well.

In another scenario, you may find that individual methods require specific RPC method name substitutions.
Methods in .NET come with certain naming restrictions. In cases where you need or want to expose your method
via RPC using a name that differs from its .NET name, you can use the `JsonRpcMethodAttribute`:

```cs
class Server
{
    [JsonRpcMethod("textDocument/References")]
    public void TextDocumentReferences(int a, int b);
}
```

In this case, the RPC client must invoke the `textDocument/References` method. The `TextDocumentReferences` name
will *not* be matched to this method, and a client's request for that name would be rejected.

Note that the automatic aliasing of methods to remove an `Async` suffix does _not_ apply to methods that use
the `JsonRpcMethodAttribute`.

## Registering individual methods

You can also register individual methods for callbacks. This works with `MethodInfo` and a target object,
or a delegate. This requires using the `JsonRpc` constructor syntax:

```cs
JsonRpc rpc = new JsonRpc(stream);
rpc.AddLocalRpcMethod("sumOf", new Func<int, int, int>((a, b) => a + b));
rpc.AddLocalRpcMethod("difference", new Func<int, int, int>((a, b) => a - b));
rpc.AddLocalRpcMethod("readFile", new Func<string, Task<string>>(path => File.ReadAllTextAsync(path)));
rpc.StartListening();
```

Note the explicit construction of delegate types in the above example. This is important since the `AddLocalRpcMethod`
takes a general `Delegate` type parameter.

### Parameter name and placement

RPC servers should consider the methods they expose to their clients as public API that requires stability.
The following changes to a method's signature can be considered breaking:

1. Renaming parameters will break clients that pass parameter by name
1. Reordering parameters will break clients that pass parameter by position
1. Removing parameters
1. Removing a method or overload
1. Adding non-optional parameters

The following changes to a method's signature can be considered **non**-breaking:

1. Adding optional parameters
1. Adding an overload
1. Changing the parameter type, if it remains compatible with the wire format representation fo the value (e.g. `int` to `double`)

### <a name="OverloadCaveats"></a>Method overloading

Method overloading is when you declare multiple methods with the same name but a different signature.
In StreamJsonRpc we support method overloads on the server-side, with some caveats.

When a client invokes a method on an object that overloads that method, in a typical program the compiler will choose the most appropriate overload based on the argument count and types being passed into the method.
Once compiled, the program will always invoke that particular method on the target object.
In JSON-RPC however where types are not specified and there is no compiler, a different overload resolution process is required.
In fact the overload resolution occurs on the server-side at runtime instead of the client side as would happen in a typical program during compilation.

When StreamJsonRpc receives an RPC request with a method name that has more than one exposed overload, the StreamJsonRpc server begins the process of overload resolution.
The process goes as follows:

1. Obtain the subset of server RPC methods with the name that matches the one in the RPC request.
1. Reject a method overload if it has fewer parameters than the number of arguments included in the request.
1. Reject a method overload if it has more required parameters than the number of arguments included in the request. Required parameters exclude a trailing `CancellationToken` and other parameters that include a default value (e.g. `string v = ""`).
1. If the request uses *named arguments* (instead of positional arguments), reject any method that has a required parameter whose name does not appear in the request's named arguments.
1. Iterate through each method parameter and request argument pair and attempt to deserialize to the parameter type. Reject any overload that fails deserialization of any argument. The success of this test depends on the serializer being used. For example Newtonsoft.Json may be very forgiving and allow an integer argument to deserialize as a string, whereas a MessagePack serializer would reject doing so.
1. Given a method overload that passes all the above criteria, choose it immediately, without further evaluating the remaining overload candidates.

**Advice**: From the foregoing, the following guidelines for defining RPC methods may help you avoid method overload mis-resolution:

1. Avoid overloading methods. This is the simplest way to avoid ever having a bad resolution take place. Simply give each method a unique name.
1. Give each overload a unique number of required parameters. This leads to the fast and simple path of choosing the right overload.
1. Ensure that overloads with the same number of required parameters have at least one parameter whose argument value could not possibly be deserialized as the parameter type of any other overload. This would leverage the slowest path on StreamJsonRpc as it tries to deserialize each argument into each overload's parameters and will pick the first working overload it finds. Write tests for each overload that leverages StreamJsonRpc to verify that every overload can be correctly invoked from the client.

### Notifications

The JSON-RPC spec allows for some requests to act as "notifications" for which no response from the server is given.
StreamJsonRpc offers this option when sending a request, but does not indicate to the server whether it is invoked
based on a notification or a request that warrants a response. StreamJsonRpc considers this an implementation detail
of the protocol and sends a response when appropriate but otherwise treats the server the same.

## Canceling all locally invoked RPC methods on connection termination

An RPC server may want to continue serving a client's request even if the client has disconnected from the server.
To abort the server method when the connection with the client dies, accept a `CancellationToken` on the server method
and set `JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed` to `true`.

## Custom configurations

Many of the foregoing examples all use the `Attach` static method, which both establishes the JSON-RPC connection
and *starts listening* for incoming messages. Incoming messages may be requests or responses to local requests.

Certain configuration changes may be dangerous to make after listening has begun. Adding target objects or individual methods
should usually be done *before* listening has started so that requests for those methods are not processed until you are
prepared to handle them.

To create a configurable `JsonRpc` instance that does not immediately start processing incoming messages, use the
`JsonRpc` constructor instead of the static `Attach` method:

```cs
var rpc = new JsonRpc(stream);

// Here, you can configure all you want, by adding targets, converters, etc.
var target = new Server();
rpc.AddLocalRpcTarget(target);

// Start listening when you are ready.
rpc.StartListening();
```

After listening has started, attempts to reconfigure `JsonRpc` will throw an `InvalidOperationException`.
This protects you from accidentally listening before adding target objects, resulting in race conditions
where requests are rejected.

```cs
var rpc = new JsonRpc(stream);
rpc.StartListening();
rpc.AddLocalRpcTarget(new Server()); // WRONG ORDER: THIS WILL THROW.
```

### Advanced configuration changes while listening to messages

If you find yourself in a scenario where you need to reconfigure `JsonRpc` after listening has started,
you may set the `JsonRpc.AllowModificationWhileListening` property to `true`, after which reconfiguration
will be allowed.

For example, suppose you have an initial target object, which has a particular method which results in
adding another target object. This would necessarily be a configuration change after listening has started.
This can be done like so:

```cs
var rpc = new JsonRpc(stream);
rpc.AddLocalRpcTarget(new Server1(rpc));
rpc.StartListening();

class Server1 {
    private readonly JsonRpc rpc;

    internal Server1(JsonRpc rpc) {
        this.rpc = rpc;
    }

    public void AddTarget2() {
        rpc.AllowModificationWhileListening = true;
        rpc.AddLocalRpcTarget(new Server2());
        rpc.AllowModificationWhileListening = false;
    }
}
```
