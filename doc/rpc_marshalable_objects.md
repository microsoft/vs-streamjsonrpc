# `RpcMarshalableAttribute` support

StreamJsonRpc typically *serializes* values that are passed in arguments or return values of RPC methods, which effectively transmits the data of an object or struct to the remote party.
By applying the `RpcMarshalableAttribute` to an interface, it a proxy can be sent to effectively marshal *behavior* to the remote party instead of data, similar to other [exotic types](exotic_types.md).

StreamJsonRpc allows transmitting marshalable objects (i.e., objects implementing a marshalable interface) in arguments and return values.

Marshalable interfaces must:

1. Extend `IDisposable`.
1. Not include any properties.
1. Not include any events.

The object that implements a marshalable interface may include properties and events as well as other additional members but only the methods defined by the marshalable interface will be available on the proxy, and the data will not be serialized.

## Use cases

In all cases, the special handling of a marshalable object only occurs if the container of that value is typed as the corresponding marshalable interface.
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

⚠️ While this use case is supported, be very wary of this pattern because it becomes less obvious to the receiver that an `IDisposable` value is tucked into the object tree of an argument somewhere that *must* be disposed to avoid a resource leak.

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

When a marshalable object instance is sent over RPC, resources are held by both parties to marshal interactions
with that object.

These resources are released and the `IDisposable.Dispose()` method is invoked on the sender's marshalable object when any of these occur:

* The receiver calls `IDisposable.Dispose()` on the proxy.
* The JSON-RPC connection is closed.

## `RpcMarshalableKnownSubType` attribute

StreamJsonRpc provides the `RpcMarshalableKnownSubType` attribute to specify that marshalable objects implementing an RPC interface can optionally implement additional intefaces.

The `RpcMarshalableKnownSubType` attribute is applied to the interface used in the RPC contract: the same interface that has the `RpcMarshalable` attribute. Only `RpcMarshalableKnownSubType` attributes applied to the interface used in the RPC contract are honored.

The interfaces `RpcMarshalableKnownSubType` attributes reference must have the `RpcMarshalable` attribute and must adhere to all the requirements of marshalable interfaces:
1. Must extend `IDisposable`.
1. Must not include any properties.
1. Must not include any events.

The proxy object created for the marshalable object will only implement the subset of interfaces described by `RpcMarshalableKnownSubType` attributes that are applied to the interface type used in the RPC method declaration. The receiver of the proxy can use the [is](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/is) operator to check if an optional interface is implemented. Casting a proxy object to an unimplemented interface will result in an `InvalidCastException`.

### Use cases

`RpcMarshalableKnownSubType` is useful in a few scenarios:
1. adding new methods to an existing marshalable interface without breaking backward compatibility,
1. creating an RPC method that return marshalable objects which implement different interfaces to accomodate for different behaviors,
1. creating an RPC method that return marshalable objects which may optionally implement additional functionalities.

For example, the following code shows how `RpcMarshalableKnownSubType` attributes can be added to the `ICounter` interface from the earlier sample.

```cs
[RpcMarshalable]
[RpcMarshalableKnownSubType(typeof(IAdvancedCounter, subTypeCode: 1))]
[RpcMarshalableKnownSubType(typeof(IDecrementable, subTypeCode: 2))]
interface ICounter : IDisposable
{
    Task IncrementAsync(CancellationToken ct);

    Task<int> GetCountAsync(CancellationToken ct);
}

[RpcMarshalable]
interface IAdvancedCounter : ICounter
{
    Task IncrementAsync(int count, CancellationToken ct);
}

[RpcMarshalable]
interface IDecrementable : IDisposable
{
    Task DecrementAsync(CancellationToken ct);
}
```

This change doesn't require any modification to the declarations of RPC methods that exchange `ICounter` object and is fully backward compatible. A client that uses an earlier RPC contract definition will simply ignore any unknow additional interface.

A marshalable object implementing `ICounter` can also implement `IAdvancedCounter`, `IDecrementable`, both or neither.

### Caveats

#### Backward compatibility

The `subTypeCode` values used in `RpcMarshalableKnownSubType` attributes are used as part of the wire protocol. While it is backward compatible to remove an `RpcMarshalableKnownSubType` attribute from an interface, its `subTypeCode` value should never be reused to add a different additional interface to the same interface declaration.

#### Method name conflicts and non-marshalable interfaces

When a method is invoked on a marshalable object proxy, it is traslated into an RPC call to the best-matching interface method.

For example, given the interfaces defined below:
- a call to `((IFoo)proxy).DoFooAsync()` would result in an RPC call to the `DoFooAsync` method as defined by the `IFoo` interface.
- a call to `((IBaz)proxy).DoFooAsync()` would result in an RPC call to the `DoFooAsync` method as defined by the `IBaz` interface, if the marshalable object implements `IBaz`.
- a call to `((IBar)proxy).DoBarAsync()` would result in an RPC call to the `DoBarAsync` method as defined by the `IBaz` interface (due to it extending `IBar`), if the marshalable object implements `IBaz`.
- a call to `((IBaz)proxy).DoBazAsync()` would result in an RPC call to the `DoBazAsync` method as defined by the `IBaz` interface, if the marshalable object implements `IBaz`.

⚠️ A call to `((IBar)proxy).DoFooAsync()` would result in an RPC call to the `DoFooAsync` method as defined by the `IBaz` interface because `IBar` is not listed in one of the `RpcMarshalableKnownSubType` attributes of `IFoo`. This is only a problem if the marshalable object explicitly implements `IBar.DoFooAsync()` and `IBaz.DoFooAsync()` as two separate methods.

```cs
[RpcMarshalable]
[RpcMarshalableKnownSubType(typeof(IBaz, subTypeCode: 1))]
interface IFoo : IDisposable
{
    Task DoFooAsync();
}

interface IBar : IFoo
{
    new Task DoFooAsync();

    Task DoBarAsync();
}

[RpcMarshalable]
interface IBaz : IBar
{
    new Task DoFooAsync();

    Task DoBazAsync();
}
```

Consider following these best practices when defining RPC marshalable interfaces:
- avoid multiple methods having the same name,
- when possible, include all interfaces in the inheritance chain in `RpcMarshalableKnownSubType` attributes of the base interface used by the RPC contract method,
- avoid marshalable objects explicitly implementing methods having the same name, defined by different interfaces, as separate methods.

A proxy has unexpected behavior only when all three of the above best practices are violated.

When invoking methods on a proxy, avoid casting the proxy to an interface that is not included in one of the `RpcMarshalableKnownSubType` attributes, especially if the method has a name conflict with a method of a descendant interface.

An especially troublesome occurrence of this issue can happen if a marshalable object explicitly implements separately two methods with the same name from two different interfaces that are not declared in one of the `RpcMarshalableKnownSubType` attributes. For example, referencing the code below, a call to `((IOther1)proxy).DoSomethingAsync()` would be indistinguishable from a call to `((IOther2)proxy).DoSomethingAsync()` and would be dispatched randomly to one of the two methods.

```cs
class MyMarshalableObject : IMerged
{
    async Task IOther1.DoSomethingAsync() {}
    async Task IOther2.DoSomethingAsync() {}
    public void Dispose() {}
}

[RpcMarshalable]
[RpcMarshalableKnownSubType(typeof(IMerged, subTypeCode: 1))]
interface IBase : IDisposable
{
}

interface IOther1
{
    Task DoSomethingAsync();
}

interface IOther2
{
    Task DoSomethingAsync();
}

[RpcMarshalable]
interface IMerged : IBase, IOther1, IOther2
{
}
```

## Protocol

The protocol for proxying a disposable object is based on [general marshaled objects](general_marshaled_objects.md).

The responsibility to release resources is on the receiver of the proxy.