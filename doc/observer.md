# `IObserver<T>` support

StreamJsonRpc allows transmitting `IObserver<T>` objects in arguments and return values.

StreamJsonRpc supports standard .NET events on target objects and interface-backed dynamic proxies.
But .NET events have several limitations:

1. The RPC target object does not know if or when the client has an event handler attached to receive these events.
1. There is no natural way for the server to convey to the client when events will stop, and whether such termination is the result of an error.
1. There is no natural way for the client to pass parameters to the server to express exactly what data they are interested in receiving events for.

The `IObservable<T>`/`IObserver<T>` pattern in .NET provides a natural solution to all these problems over RPC.

Learn more about the [Observer design pattern](https://docs.microsoft.com/en-us/dotnet/standard/events/observer-design-pattern).

## Use cases

### Subscription

StreamJsonRpc supports the observable pattern by allowing `IObserver<T>` to be used by RPC methods as arguments.
For example an RPC target object may be defined like this:

```cs
class RpcServer : IDisposable
{
    private readonly CancellationTokenSource disposalSource = new CancellationTokenSource();
    private readonly List<IObserver<int>> observers = new List<IObserver<int>>();

    public RpcServer()
    {
        this.LongRunningStateMutatingProcess();
    }

    void IDisposable.Dispose() => this.disposalSource.Cancel();

    public void Subscribe(IObserver<int> observer)
    {
        lock (this.observers)
        {
            this.observers.Add(observer);
        }
    }

    private async Task LongRunningStateMutatingProcess()
    {
        // Simulate some long-running state changes that
        // periodically raise notifications to observers.
        try
        {
            for (int i = 1; !this.disposalSource.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, this.disposalSource.Token);
                lock (this.observers)
                {
                    foreach (IObserver<int> observer in observers)
                    {
                        observer.OnNext(i);
                    }
                }
            }

            // Notify observers that the sequence is over.
            lock (this.observers)
            {
                foreach (IObserver<int> observer in this.observers)
                {
                    observer.Completed();
                }

                this.observers.Clear();
            }
        }
        catch (Exception ex)
        {
            lock (this.observers)
            {
                foreach (IObserver<int> observer in this.observers)
                {
                    observer.OnError(ex);
                }

                this.observers.Clear();
            }
        }
    }
}
```

### Canceling subscriptions

To further support the `IObservable<T>` pattern, these subscribe [methods may return `IDisposable`](disposable.md) so that the RPC client may cancel their subscription by disposing the returned value.
The above RPC server might be enhanced to support such a pattern by replacing the Subscribe method with this:

```cs
class RpcServer : IDisposable, IObservable<int>
{
    public IDisposable Subscribe(IObserver<int> observer)
    {
        lock (this.observers)
        {
            this.observers.Add(observer);
        }

        return new DisposeAction(delegate
        {
            lock (this.observers)
            {
                this.observers.Remove(observer);
            }
        });
    }

    private class DisposeAction : IDisposable
    {
        private readonly Action disposeAction;

        internal DisposeAction(Action disposeAction)
        {
            this.disposeAction = disposeAction;
        }

        public void Dispose() => this.disposeAction();
    }
}
```

Note that a `CancellationToken` parameter on the RPC method itself is only marshaled from the client during execution of the RPC method itself.
Once the server method returns (or the Task it returns completes) the `CancellationToken` is dead.
For this reason, do not use it as a means for the client to cancel its subscription unless the server method
is async and is designed to remain active until the subscription is complete or canceled.

## Resource leaks concerns

When an `IObserver<T>` instance is sent over RPC, resources are held by both parties to marshal interactions
with that object.

These resources are released when any of these occur:

1. The receiver calls `IObserver<T>.OnCompleted()` or `IObserver<T>.OnError(Exception)` on the object.
1. The RPC client that sent the object as an argument receives an error response from the server.
1. The JSON-RPC connection is closed.

`IObserver<T>` may *not* be sent as an argument to a server method that is invoked as a notification.
An attempt to do so will be rejected by the client to minimize the risk of a resource leak when
the server either does not implement the named method or throws while invoking it,
since the client gets no feedback from the server in either of these cases.

## Protocol

The protocol for proxying a disposable object is based on [general marshaled objects](general_marshaled_objects.md).

The responsibility to release resources when `IObserver<T>.OnCompleted()` or `IObserver<T>.OnError(Exception)` is called is on the receiver of the proxy.
That is, when the proxy holder calls either of these methods, it should also send the `$/releaseMarshaledObject` message back to the target object owner. When the proxy holder is using StreamJsonRpc, this is handled automatically.
