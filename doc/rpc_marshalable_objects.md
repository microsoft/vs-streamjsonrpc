# `RpcMarshalableAttribute` support

StreamJsonRpc allows the definition of marshalable interfaces: interfaces marked with `RpcMarshalableAttribute`.

StreamJsonRpc allows transmitting marshalable objects (i.e., objects implementing a marshalable interface) in arguments and return values.

Marshalable interfaces must:

1. Extend `IDisposable`.
1. Not include any properties.
1. Not include any event.

The marshalable object can include properties and events as well as other additional members but only the methods defined by the marshalable interface will be available on the proxy.

## Use cases

In all cases, the special handling of a marshalable object only occurs if the container of that value is typed as the corrsponding marshalable interface.
This means that an object that implements a marshalable interface will not necessarily be marshaled instead of serialized.

For each use case, assume `Counter` is a class defined for demonstration purposes like this:

```cs
class Counter : ICounter
{
    private int count;

    public bool IsDisposed { get; private set; }

    public event EventHandler? IncrementedEvent;

    public event EventHandler? DisposedEvent;

    public Task IncrementAsync(CancellationToken ct)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(null);
        }

        count++;
        IncrementedEvent?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<int> GetCountAsync(CancellationToken ct)
    {
        return Task.FromResult(count);
    }

    void IDisposable.Dispose()
    {
        if (IsDisposed is false)
        {
            DisposedEvent?.Invoke(this, EventArgs.Empty);
            IsDisposed = true;
        }
    }
}

[RpcMarshalable]
interface ICounter : IDisposable
{
    Task IncrementAsync(CancellationToken ct);

    Task<int> GetCountAsync(CancellationToken ct);
}
```

### Method return value

In the simplest case, the RPC server returns a marshalable interface object.

```cs
interface IRpcServer
{
    ICounter GetCounter();
}

class RpcServer : IRpcServer
{
    private int activeCounters;

    public ICounter GetCounter()
    {
        var counter = new Counter();
        counter.IncrementedEvent += OnIncrement;
        counter.DisposedEvent += OnCounterDisposed;
        activeCounters++;
        return counter;
    }

    private OnIncrement(object sender, EventArgs e)
    {
        // Do something
    }

    private OnCounterDisposed(object sender, EventArgs e)
    {
        counter.IncrementedEvent -= OnIncrement;
        counter.DisposedEvent -= OnCounterDisposed;
        activeCounters--;
    }
}
```

### Method argument

In this use case the RPC *client* provides the marshalable object to the server:

```cs
interface IRpcContract
{
    Task ProvideCounterAsync(ICounter counter);
}

IRpcContract client = jsonRpc.Attach<IRpcContract>();
var counter = new Counter();
await client.ProvideCounterAsync(counter);
```

### Value within a single argument's object graph

In this use case the RPC client again provides the marshalable object to the server,
but this time it passes it as a property of an object used as the argument.

```cs
class SomeClass
{
    public ICounter Counter { get; set; }
}

interface IRpcContract
{
    Task ProvideClassAsync(SomeClass value);
}

IRpcContract client = jsonRpc.Attach<IRpcContract>();
var arg = new SomeClass
{
    Counter = new Counter(),
};
await client.ProvideClassAsync(arg);
```

While this use case is supported, be very wary of this pattern because it becomes less obvious to the receiver that an `IDisposable` value is tucked into the object tree of an argument somewhere that *must* be disposed to avoid a resource leak.

### As an argument without a proxy for an RPC interface

When you are not using an RPC interface and dynamically generated proxy that implements it, you can still pass a marshalable object as an argument by explicitly passing in the declared parameter types to the `InvokeWithCancellationAsync` call:

```cs
ICounter arg = new Counter();
await jsonRpc.InvokeWithCancellationAsync(
    "methodName",
    new object?[] { arg },
    new Type[] { typeof(ICounter) },
    cancellationToken);
```

### Invalid cases

Here are some examples of where an object that implements a marshalable interface is serialized (i.e. by value) instead of being marshaled (i.e. by reference).

In this example, although `ByValueCounter` implements `ICounter`, its declared parameter type is `ByValueCounter`:

```cs
[DataContract]
class ByValueCounter : ICounter
{
    [DataMember]
    public int Count { get; set; }

    public bool IsDisposed { get; private set; }

    public Task IncrementAsync(CancellationToken ct)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(null);
        }

        Count++;
        return Task.CompletedTask;
    }

    public Task<int> GetCountAsync(CancellationToken ct)
    {
        return Task.FromResult(Count);
    }

    void IDisposable.Dispose()
    {
        IsDisposed = true;
    }
}

ICounter arg = new ByValueCounter();
await jsonRpc.InvokeWithCancellationAsync(
    "methodName",
    new object?[] { arg },
    new Type[] { typeof(ByValueCounter) },
    cancellationToken);
```

Here is the same situation with an RPC interface:

```cs
interface IRpcContract
{
    Task ProvideCounterbyValueAsync(ByValueCounter value);
}

IRpcContract client = jsonRpc.Attach<IRpcContract>();
ByValueCounter arg = new ByValueCounter();
await client.ProvideCounterbyValueAsync(arg);
```

Or similar for an object returned from an RPC server method:

```cs
class RpcServer
{
    public ByValueCounter GetCounterByValue() => new ByValueCounter();
}
```

In each of these cases, the receiving part will get a `ByValueCounter` object, but calling any method on that object will be a local call to that object rather than being remoted back to the original object. Also, disposing the received object or closing the JSON-RPC connection will not dispose the original object.

## Resource leaks concerns

When an marshalable object instance is sent over RPC, resources are held by both parties to marshal interactions
with that object.

These resources are released and the `IDisposable.Dispose()` method is invoked on the sender's marshalable object when any of these occur:

1. The receiver calls `IDisposable.Dispose()` on the proxy.
1. The JSON-RPC connection is closed.

## Protocol

The protocol for proxying a disposable object is based on [general marshaled objects](general_marshaled_objects.md).

The responsibility to release resources is on the receiver of the proxy.
That is, when the proxy holder calls either of these methods, it should also send the `$/releaseMarshaledObject` message back to the target object owner. When the proxy holder is using StreamJsonRpc, this is handled automatically when `IDisposable.Dispose()` is invoked on the proxy.
