# `RpcMarshalableAttribute`

StreamJsonRpc typically *serializes* values that are passed in arguments or return values of RPC methods, which effectively transmits the data of an object or struct to the remote party.
By applying the <xref:StreamJsonRpc.RpcMarshalableAttribute> to an interface, it a proxy can be sent to effectively marshal *behavior* to the remote party instead of data, similar to other [exotic types](index.md).

StreamJsonRpc allows transmitting marshalable objects (i.e., objects implementing a marshalable interface) in arguments and return values.

Marshalable interfaces must:

1. Extend <xref:System.IDisposable> (unless interface is call-scoped).
1. Not include any properties.
1. Not include any events.

The object that implements a marshalable interface may include properties and events as well as other additional members but only the methods defined by the marshalable interface will be available on the proxy, and the data will not be serialized.

The <xref:StreamJsonRpc.RpcMarshalableAttribute> must be applied directly to the interface used as the return type, parameter type, or member type within a return type or parameter type's object graph.
The attribute is not inherited.
In fact different interfaces in a type hierarchy can have this attribute applied with distinct settings, and only the settings on the attribute applied directly to the interface used will apply.

## Call-scoped vs. explicitly scoped

### Explicit lifetime

An RPC marshalable interface has an explicit lifetime by default.
This means that the receiver of a marshaled object owns its lifetime, which may extend beyond an individual RPC call.
Memory for the marshaled object and its proxy are not released until the receiver either disposes of its proxy or the JSON-RPC connection is closed.

### Call-scoped lifetime

A call-scoped interface produces a proxy that is valid only during the RPC call that delivered it.
It may only be used as part of a method request as or within an argument.
Using it as or within a return value or exception will result in an error.

This is the preferred model when an interface is expected to only be used within request arguments because it mitigates the risk of a memory leak due to the receiver failing to dispose of the proxy.
This model also allows the sender to retain control over the lifetime of the marshaled object.

Special allowance is made for `IAsyncEnumerable<T>`-returning RPC methods so that the lifetime of the marshaled object is extended to the lifetime of the enumeration.
An `IAsyncEnumerable<T>` in an exception thrown from the method will *not* have access to the call-scoped marshaled object because exceptions thrown by the server always cause termination of objects marshaled by the request.

Opt into call-scoped lifetimes by setting the `CallScopedLifetime` property to `true` on the attribute applied to the interface:

```css
[RpcMarshalable(CallScopedLifetime = true)]
```

It is not possible to customize the lifetime of an RPC marshaled object except on its own interface.
For example, applying this attribute to the parameter that uses the interface is not allowed.

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

Call-scoped marshalable interfaces may not be used as a return type or member of its object graph.

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

Call-scoped marshalable interfaces may only appear as a method parameter or a part of its object graph.

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

⚠️ While this use case is supported, be very wary of this pattern because it becomes less obvious to the receiver that an <xref:System.IDisposable> value is tucked into the object tree of an argument somewhere that *must* be disposed to avoid a resource leak.
This risk can be mitigated by using call-scoped marshalable interfaces.

### As an argument without a proxy for an RPC interface

When you are not using an RPC interface and generated proxy that implements it, you can still pass a marshalable object as an argument by explicitly passing in the declared parameter types to the @StreamJsonRpc.JsonRpc.InvokeWithCancellationAsync* call:

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

## <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute>

StreamJsonRpc provides the <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> to specify that marshalable objects implementing an RPC interface can optionally implement additional interfaces.

<xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> is applied to the interface used in the RPC contract.
Such an interface must also apply the <xref:StreamJsonRpc.RpcMarshalableAttribute>.

An interface that <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> references must have the <xref:StreamJsonRpc.RpcMarshalableAttribute> attribute applied, set <xref:StreamJsonRpc.RpcMarshalableAttribute.IsOptional> to `true`, and must adhere to all the requirements of marshalable interfaces as described earlier in this document.

The proxy object created for the receiver of a marshaled object will implement all the interfaces that are both implemented by the original object and that are identified with <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> on the interface type used in the RPC contract.

The receiver of the proxy should avoid using standard type check or cast operators such as [is](https://learn.microsoft.com/dotnet/csharp/language-reference/operators/is) to check if an optional interface is implemented because a proxy *may* implement more interfaces than the marshaled object does.
Instead, test proxies for optional interfaces using the <xref:StreamJsonRpc.IClientProxy.Is(System.Type)> method or <xref:StreamJsonRpc.JsonRpcExtensions.As``1(StreamJsonRpc.IClientProxy)> extension method.

An <xref:StreamJsonRpc.RpcMarshalableAttribute> interface that also carries <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> attributes will lead to generation of extension methods for itself and for each of the optional interfaces.
These extension methods expose the <xref:StreamJsonRpc.IClientProxy.Is(System.Type)> and <xref:StreamJsonRpc.JsonRpcExtensions.As``1(StreamJsonRpc.IClientProxy)> methods so they are conveniently accessible via these RPC interfaces more directly so that conditional casting to <xref:StreamJsonRpc.IClientProxy> is not required at each callsite.

The `PublicRpcMarshalableInterfaceExtensions` MSBuild property can be set to `true` in the project file to cause these extension methods to be declares on a `public` extension class rather than an `internal` one, so that referencing assemblies that use these proxies can have the same level of convenience in testing for optional interfaces.

### Use cases

<xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> is useful in a few scenarios:

1. when adding new methods to an existing marshalable interface would break backward compatibility for another scenario,
1. when the host of the marshaled object may deem it appropriate to expose different behaviors based on the scenario,
1. when an RPC method returns marshalable objects which may optionally implement additional functionality based on the arguments passed to that method.

For example, the following code shows how <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> can be added to the `ICounter` interface from the earlier sample:

```cs
[RpcMarshalable]
[RpcMarshalableOptionalInterface(optionalInterfaceCode: 1, typeof(IAdvancedCounter))]
[RpcMarshalableOptionalInterface(optionalInterfaceCode: 2, typeof(IDecrementable))]
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

This change doesn't require any modification to the declarations of RPC methods that exchange `ICounter` objects and is fully backward compatible.
A client that uses an earlier RPC contract definition will simply ignore any unknown additional interface.

A marshalable object implementing `ICounter` can also implement `IAdvancedCounter`, `IDecrementable`, both or neither.
The proxy receiver will be able to perceive through type checks which interfaces are available.

### Caveats

#### Backward compatibility

The `optionalInterfaceCode` values used in <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> are used as part of the wire protocol.
While it can be backward compatible to remove an <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> from an interface, its `optionalInterfaceCode` value should never be reused to add a different interface to the same interface declaration to avoid an older remote party misinterpreting the value as identifying the older interface.

#### Method name conflicts and non-marshalable interfaces

When a method is invoked on a marshalable object proxy, it is translated into an RPC call to the best-matching interface method.

We'll use a few interfaces to describe interesting corner cases.
Note that we do *not* recommend reusing method names across interfaces or redeclaring the same methods in derived interfaces, but we include them here to document behaviors you can expect from doing so.
Consider these interfaces:

```cs
[RpcMarshalable]
[RpcMarshalableOptionalInterface(optionalInterfaceCode: 1, typeof(IBaz))]
[RpcMarshalableOptionalInterface(optionalInterfaceCode: 2, typeof(IBaz2))]
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

Given the interfaces above, a call to any of the following methods behave as expected:

Method call | Invoked server-side method | Conditions
------ | ------ | ------
`((IFoo)proxy).DoFooAsync()` | `DoFooAsync` as defined by `IFoo` |
`((IBaz)proxy).DoFooAsync()` | `DoFooAsync` as defined by `IBaz` | If the marshalable object implements `IBaz`
`((IBaz2)proxy).DoFooAsync()` | `DoFooAsync` as defined by `IBaz2` | If the marshalable object implements `IBaz2`
`((IBar)proxy).DoBarAsync()` | `DoBarAsync` as defined by `IBar` | If the marshalable object implements `IBaz`, `IBa2`, or both
`((IBaz)proxy).DoBarAsync()` | `DoBarAsync` as defined by `IBar` | If the marshalable object implements `IBaz`
`((IBaz2)proxy).DoBarAsync()` | `DoBarAsync` as defined by `IBar` | If the marshalable object implements `IBaz2`
`((IBaz)proxy).DoBazAsync()` | `DoBazAsync` as defined by `IBaz` | If the marshalable object implements `IBaz`
`((IBaz2)proxy).DoBazAsync()` | `DoBazAsync` as defined by `IBaz2` | If the marshalable object implements `IBaz2`

⚠️ An attempt to cast proxy to `IBar` would fail, even if the original object implemented that interface, if that object did not also implement `IBaz` or  `IBaz2`, since `IBar` is not listed in an <xref:StreamJsonRpc.RpcMarshalableOptionalInterfaceAttribute> of `IFoo`.

A call to `((IBar)proxy).DoFooAsync()` would result in the following behavior:

Implemented interfaces | Invoked server-side method
------ | ------
`IBaz` | `DoFooAsync` as defined by `IBaz`
`IBaz2` | `DoFooAsync` as defined by `IBaz2`
`IBaz` and `IBaz2` | **Undefined behavior**: `DoFooAsync` as defined by either `IBaz` or `IBaz2`

The issue described above is only a problem if the marshalable object explicitly implements any of the `IBar.DoFooAsync()`, `IBaz.DoFooAsync()` or `IBaz2.DoFooAsync()` methods.

Consider following these best practices when defining RPC marshalable interfaces:

* Avoid multiple methods having the same name.
* When possible, include all interfaces in the inheritance chain in `RpcMarshalableOptionalInterface` attributes of the base interface used by the RPC contract method.
* Avoid marshalable objects explicitly implementing methods having the same name, defined by different interfaces, as separate methods.

In short, when all three of the above best practices are violated, the method chosen on a host object to respond to a request may surprise you.

When invoking methods on a proxy, avoid casting the proxy to an interface that is not included in one of the `RpcMarshalableOptionalInterface` attributes, especially if the method has a name conflict with a method of a descendant interface.

An especially troublesome occurrence of this issue can happen if a marshalable object explicitly implements separately two methods with the same name from two different interfaces that are not declared in one of the `RpcMarshalableOptionalInterface` attributes. For example, referencing the code below, a call to `((IOther1)proxy).DoSomethingAsync()` would be indistinguishable from a call to `((IOther2)proxy).DoSomethingAsync()` and would be dispatched to one of the two methods in an undefined manner.

```cs
class MyMarshalableObject : IMerged
{
    async Task IOther1.DoSomethingAsync() {}
    async Task IOther2.DoSomethingAsync() {}
    public void Dispose() {}
}

[RpcMarshalable]
[RpcMarshalableOptionalInterface(optionalInterfaceCode: 1, typeof(IMerged))]
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
