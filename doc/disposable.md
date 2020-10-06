# `IDisposable` support

StreamJsonRpc allows marshaling `IDisposable` objects in arguments and return values.

An `IDisposable` object may also implement `INotifyDisposable` on the sender side
so the sender can terminate the lifetime of the marshalled object to release resources.

## Use cases

In all cases, the special handling of an `IDisposable` value only occurs if the container of that value is typed as `IDisposable`.
This means that an object that implements `IDisposable` will not necessarily be marshaled instead of serialized.
Consider each of the below use cases to see how the value can be *typed* as `IDisposable`.

For each use case, assume `DisposeAction` is a class defined for demonstration purposes like this:

```cs
class DisposeAction : IDisposable
{
    private readonly Action disposeAction;

    internal DisposeAction(Action disposeAction)
    {
        this.disposeAction = disposeAction;
    }

    public void Dispose() => this.disposeAction();
}
```

### Method return value

In the simplest case, the RPC server returns an `IDisposable` object that gets disposed on the server
when the client disposes it.

```cs
class RpcServer
{
    public IDisposable GetDisposable() => new DisposeAction(() => { /* The client disposed us */ });
}
```

### Method argument

In this use case the RPC *client* provides the `IDisposable` value to the server:

```cs
interface IRpcContract
{
    Task ProvideDisposableAsync(IDisposable value);
}

IRpcContract client = jsonRpc.Attach<IRpcContract>();
IDisposable arg = new DisposeAction(() => { /* the RPC server called Dispose() on the argument */});
await client.ProvideDisposableAsync(arg);
```

### Value within a single argument's object graph

In this use case the RPC client again provides the `IDisposable` value to the server,
but this time it passes it as a property of an object used as the argument.

```cs
class SomeClass
{
    public IDisposable DisposableValue { get; set; }
}

interface IRpcContract
{
    Task ProvideClassAsync(SomeClass value);
}

IRpcContract client = jsonRpc.Attach<IRpcContract>();
var arg = new SomeClass
{
    DisposableValue = new DisposeAction(() => { /* the RPC server called Dispose() on the argument */}),
};
await client.ProvideClassAsync(arg);
```

While this use case is supported, be very wary of this pattern because it becomes less obvious to the receiver that an `IDisposable` value is tucked into the object tree of an argument somewhere that *must* be disposed to avoid a resource leak.

### As an argument without a proxy for an RPC interface

When you are not using an RPC interface and dynamically generated proxy that implements it, you can still pass a marshaled `IDisposable` value as an argument by explicitly passing in the declared parameter types to the `InvokeWithCancellationAsync` call:

```cs
IDisposable arg = new DisposeAction(() => { /* the RPC server called Dispose() on the argument */});
await jsonRpc.InvokeWithCancellationAsync(
    "methodName",
    new object?[] { arg },
    new Type[] { typeof(IDisposable) },
    cancellationToken);
```

### Invalid cases

Here are some examples of where an object that implements `IDisposable` is serialized (i.e. by value) instead of being marshaled (i.e. by reference).

In this example, although `Data` implements `IDisposable`, its declared parameter type is `Data`:

```cs
class Data : IDisposable { /* ... */ }

IDisposable arg = new Data();
await jsonRpc.InvokeWithCancellationAsync(
    "methodName",
    new object?[] { arg },
    new Type[] { typeof(Data) },
    cancellationToken);
```

Here is the same situation with an RPC interface:

```cs
class Data : IDisposable { /* ... */ }

interface IRpcContract
{
    Task ProvideDisposableAsync(Data value);
}

IRpcContract client = jsonRpc.Attach<IRpcContract>();
Data arg = new Data();
await client.ProvideDisposableAsync(arg);
```

Or similar for an object returned from an RPC server method:

```cs
class Data : IDisposable { /* ... */ }

class RpcServer
{
    public Data GetDisposableData() => new Data();
}
```

In each of these cases, the receiving part will get a `Data` object that implements `IDisposable`, but calling `Dispose` on that object will be a local call to that object rather than being remoted back to the original object.

## Resource leaks concerns

When an `IDisposable` instance is sent over RPC, resources are held by both parties to marshal interactions
with that object.

These resources are released when any of these occur:

1. The receiver calls `IDisposable.Dispose()` on the object.
1. The JSON-RPC connection is closed.

To enable the sender to terminate the connection with the proxy to release resources, the sender should call `JsonRpc.MarshalWithControlledLifetime<T>` to wrap the `IDisposable` before sending it.

## Protocol

The protocol for proxying a disposable object is based on [general marshaled objects](general_marshaled_objects.md) with:

1. A camel-cased method name transform applied.
1. Lifetime of the proxy controlled by the receiver.
