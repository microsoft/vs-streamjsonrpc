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

## `RpcMarshalableKnownSubTypeAttribute`

StreamJsonRpc provides the `RpcMarshalableKnownSubTypeAttribute` to specify that marshalable objects implementing an RPC interface can optionally implement additional interfaces.

`RpcMarshalableKnownSubTypeAttribute` is applied to the interface used in the RPC contract.
Such an interface must also apply the `RpcMarshalableAttribute`.

An interface that `RpcMarshalableKnownSubTypeAttribute` references must have the `RpcMarshalableAttribute` attribute and must adhere to all the requirements of marshalable interfaces as described earlier in this document.

The proxy object created for the receiver of a marshaled object will implement all the interfaces that are both implemented by the original object and that are identified with `RpcMarshalableKnownSubTypeAttribute` on the interface type used in the RPC contract.
The receiver of the proxy can use the [is](https://learn.microsoft.com/dotnet/csharp/language-reference/operators/is) operator to check if an optional interface is implemented without throwing an `InvalidCastException` when the interface is not implemented.

### Use cases

`RpcMarshalableKnownSubTypeAttribute` is useful in a few scenarios:

1. when adding new methods to an existing marshalable interface would breaking backward compatibility for another scenario,
1. when the host of the marshaled object may deem it appropriate to expose different behaviors based on the scenario,
1. when an RPC method returns marshalable objects which may optionally implement additional functionality based on the arguments passed to that method.

For example, the following code shows how `RpcMarshalableKnownSubTypeAttribute` can be added to the `ICounter` interface from the earlier sample:

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

This change doesn't require any modification to the declarations of RPC methods that exchange `ICounter` object and is fully backward compatible.
A client that uses an earlier RPC contract definition will simply ignore any unknown additional interface.

A marshalable object implementing `ICounter` can also implement `IAdvancedCounter`, `IDecrementable`, both or neither.
The proxy receiver will be able to perceive through type checks which interfaces are available.

### Caveats

#### Backward compatibility

The `subTypeCode` values used in `RpcMarshalableKnownSubTypeAttribute` are used as part of the wire protocol.
While it can be backward compatible to remove an `RpcMarshalableKnownSubTypeAttribute` from an interface, its `subTypeCode` value should never be reused to add a different interface to the same interface declaration to avoid an older remote party misinterpreting the value as identifying the older interface.

#### Method name conflicts and non-marshalable interfaces

When a method is invoked on a marshalable object proxy, it is translated into an RPC call to the best-matching interface method.

We'll use a few interfaces to describe interesting corner cases.
Note that we do *not* recommend reusing method names across interfaces or redeclaring the same methods in derived interfaces, but we include them here to document behaviors you can expect from doing so.
Consider these interfaces:

```cs
[RpcMarshalable]
[RpcMarshalableKnownSubType(typeof(IBaz, subTypeCode: 1))]
[RpcMarshalableKnownSubType(typeof(IBaz2, subTypeCode: 1))]
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

[RpcMarshalable]
interface IBaz2 : IBar
{
    new Task DoFooAsync();

    Task DoBazAsync();
}
```

Given the interfaces above, RPC calls would have these behaviors:

* a call to `((IFoo)proxy).DoFooAsync()` would result in an RPC call to the `DoFooAsync` method as defined by the `IFoo` interface.
* a call to `((IBaz)proxy).DoFooAsync()` would result in an RPC call to the `DoFooAsync` method as defined by the `IBaz` interface, if the marshalable object implements `IBaz`.
* a call to `((IBaz2)proxy).DoFooAsync()` would result in an RPC call to the `DoFooAsync` method as defined by the `IBaz2` interface, if the marshalable object implements `IBaz2`.
* a call to `((IBaz)proxy).DoBarAsync()`, `((IBaz2)proxy).DoBarAsync()` or `((IBar)proxy).DoBarAsync()` would result in an RPC call to the `DoBarAsync` method as defined by the `IBar` interface, if the marshalable object implements required interfaces (`IBaz`, `IBaz2`, or any of `IBaz` or `IBar`, respectively).
* a call to `((IBaz)proxy).DoBazAsync()` would result in an RPC call to the `DoBazAsync` method as defined by the `IBaz` interface, if the marshalable object implements `IBaz`.
* a call to `((IBaz2)proxy).DoBazAsync()` would result in an RPC call to the `DoBazAsync` method as defined by the `IBaz2` interface, if the marshalable object implements `IBaz2`.
* ⚠️ An attempt to cast proxy to `IBar` would fail, even if the original object implemented that interface, if that object did not also implement `IBaz`, since `IBaz` is the only optional interface with an `RpcMarshalableKnownSubTypeAttribute`.
* ⚠️ A call to `((IBar)proxy).DoFooAsync()` would result in the following behavior:

Implemented interfaces | Result
------ | ------
`IBaz` | RPC call to the `DoFooAsync` method as defined by the `IBaz` interface
`IBaz2` | RPC call to the `DoFooAsync` method as defined by the `IBaz2` interface
`IBaz, IBaz2` | **Undefined behavior**: RPC call to the the `DoFooAsync` method as defined by the either `IBaz` or `IBaz2` interface

The issue described above is only a problem if the marshalable object explicitly implements any of the `IBar.DoFooAsync()`, `IBaz.DoFooAsync()` or `IBaz2.DoFooAsync()` methods.

Consider following these best practices when defining RPC marshalable interfaces:

* Avoid multiple methods having the same name.
* When possible, include all interfaces in the inheritance chain in `RpcMarshalableKnownSubType` attributes of the base interface used by the RPC contract method.
* Avoid marshalable objects explicitly implementing methods having the same name, defined by different interfaces, as separate methods.

In short, when all three of the above best practices are violated, the method chosen on a host object to respond to a request may surprise you.

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
