# Spec proposal: General marshaled objects

## Use cases

**The following content would belong to general_marshaled_objects.md, but StreamJsonRpc does not yet support these use cases.**

### Marshaling an object in an RPC argument with a lifetime scoped to the method call

Marshaled objects passed in arguments in _requests_ (not notifications) may be scoped to live only as long as that method call.
This is the simplest model and requires no special handling to avoid resource leaks.

To marshal an object instead of serialize its value, wrap the object with a marshalable wrapper using the
`T JsonRpc.MarshalLimitedArgument<T>(T marshaledObject)` static method.

```cs
ISomethingInteresting anObject;
await jsonRpc.InvokeAsync(
    "DoSomething",
    new object[]
    {
        1,
        JsonRpc.MarshalLimitedArgument(anObject),
        3,
    }
)
```

The above will allow methods defined on the `ISomethingInteresting` interface to be invoked on `anObject` by the RPC server's `DoSomething` method.
After `DoSomething` returns, `anObject` can no longer be invoked by the RPC server.
No explicit release is required.

### Marshaling an object with an independently controlled lifetime

Marshaled objects passed in return values or in arguments where they must remain marshaled beyond the scope of the RPC request can do so.
This mode requires that the sender and/or receiver explicitly release the marshaled object from its RPC connection in order to reclaim resources.

Use the `IRpcMarshaledContext<T> JsonRpc.MarshalWithControlledLifetime<T>(T marshaledObject)` static method prepare an object to be marshaled.
The resulting `IRpcMarshaledContext<T>` contains both the object to transmit and a method the sender may use to later terminate the marshaling relationship.

In the following example, an `IDisposable` object is marshaled via a return value from an RPC method.
Notice how the server holds onto the `IRpcMarshaledContext<T>` object so it can release the marshaled object when the subscription is over.

```cs
class RpcServer
{
    private readonly Dictionary<Subscription, IRpcMarshaledContext<IDisposable>> subscriptions = new Dictionary<Subscription, IRpcMarshaledContext<IDisposable>>();

    public IDisposable Subscribe()
    {
        var subscription = new Subscription();
        var marshaledContext = JsonRpc.MarshalWithControlledLifetime<IDisposable>(subscription);
        lock (this.subscriptions)
        {
            this.subscriptions.Add(subscription, marshaledContext);
        }

        return marshaledContext.Proxy;
    }

    // Invoked by the RpcServer when the subscription is over and can be closed.
    private void OnSubscriptionCompleted(Subscription subscription)
    {
        IRpcMarshaledContext<IDisposable> marshaledContext;
        lock (this.subscriptions)
        {
            marshaledContext = this.subscriptions[subscription]
            this.subscription.Remove(subscription);
        }

        marshaledContext.Dispose();
    }
}
```

When an object that would otherwise be marshaled is returned from an RPC server method that was invoked by a notification, no marshaling occurs and no error is raised anywhere.

### Sending a marshaled object's proxy back to its owner

The receiver of a marshaled object *may* send the proxy they receive back to its originator.
This may be done within an argument or return value.
When this is done, the receiver of that message (i.e. the original marshaled object owner) will receive a reference to the original marshaled object.

### Invoking or referencing a discarded marshaled object

When a method on a marshaled proxy is invoked or the proxy is referenced in an argument of an outbound RPC request *after* it has been disposed of, an `ObjectDisposedException` is thrown.

When the local proxy has not been disposed of but the marshaled object owner has released the target object, a `RemoteInvocationException` may be thrown with the `ErrorCode` property set to `JsonRpcErrorCode.NoMarshaledObjectFound`.
