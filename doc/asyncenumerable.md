# `IAsyncEnumerable<T>` support

StreamJsonRpc allows transmitting `IAsyncEnumerable<T>` objects in requests and response messages.
This "Just Works" but some important considerations should be taken.

This allows for the transmission of expensive or large sequences/collections while only paying
the generation cost for the values actually enumerated by the receiver.
It also helps to keep individual JSON-RPC message sizes small by breaking up a large collection result
across several messages.

## Use cases

It is recommended to use strongly-typed proxies (such as [dynamic proxies](dynamicproxy.md))
to invoke RPC methods that include `IAsyncEnumerable<T>` types.
This helps ensure that the expected parameter and return types are agreed upon by both sides.

### C# 8 async iterator methods

A server method which returns an async enumeration may be defined as:

```cs
IAsyncEnumerable<int> GenerateNumbersAsync(CancellationToken cancellationToken);
```

The server method may be implemented in C# 8 as:

```cs
public async IAsyncEnumerable<int> GenerateNumbersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
{
    for (int i = 1; i <= 20; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return i;
    }
}
```

Notice how it is not necessary (or desirable) to wrap the resulting `IAsyncEnumerable<T>` in a `Task<T>` object.

C# 8 lets you consume such an async enumerable using `await foreach`:

```cs
await foreach (int number in this.clientProxy.GenerateNumbersAsync(token))
{
    Console.WriteLine(number);
}
```

All the foregoing is simple C# 8 async enumerable syntax and use cases.
StreamJsonRpc lets you use this natural syntax over an RPC connection.

A remoted `IAsyncEnumerable<T>` can only be enumerated once.
Calling `IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken)` more than once will
result in an `InvalidOperationException` being thrown.

### Transmitting large collections

Most C# iterator methods return `IEnumerable<T>` and produce values synchronously.
An async iterator method returns `IAsyncEnumerable<T>` and is useful when producing values is expensive
or requires I/O to fetch or produce those values.

When you have an existing collection of items or you can produce items cheaply, sending them as
a collection or `IEnumerable<T>` over an RPC connection results in the entire collection being sent
as a JSON array in a single JSON-RPC message. For a large collection this may be undesirable
when it makes the message so large that other messages can't be sent in the meantime, or when
the client is likely to only want a subset of that collection.

Exposing any collection or `IEnumerable<T>` as an `IAsyncEnumerable<T>` changes the RPC behavior
from transmitting the entire collection at once to the streaming, client-pull model so only items
the receiver wants are produced and transmitted. This can be done using our
`IEnumerable<T>.AsAsyncEnumerable()` extension method, like so:

```cs
IList<int> allMyData; // set elsewhere
await clientProxy.SendCollectedDataAsync(allMyData.AsAsyncEnumerable());
```

This extension method also works when *returning* collections as the result of an RPC call.

The receiver should always enter an `await foreach` loop over this enumeration or manually get the iterator
and dispose it in order to avoid a resource leak on the sender.

## Comparison with `IProgress<T>`

StreamJsonRpc also supports [`IProgress<T>` support](progresssupport.md) parameters.
When a server method wants to return streaming results, accepting an `IProgress<T>` argument
and returning `IAsyncEnumerable<T>` are valid options. To choose, review these design considerations:

| Area | `IProgress<T>` | `IAsyncEnumerable<T>` |
|--|--|--|
| Pattern | Typically an optional argument for a caller to pass into a method and is used to get details on how the operation is progressing so that a human operator gets visual feedback on a long-running operation. | Typically used as a required argument or return type to provide data that the caller is expected to process to be functionally complete.
| Placement | Parameter or member of object used as a parameter. | Parameter, return type, or member of an object used as one.
| Lifetime | Works until the RPC method has completed, or the connection drops. | Works until disposed, or the connection drops.
| Resource leak risk | Resources are automatically released at the completion of the RPC method. | Resources leak unless the `IAsyncEnumerator<T>.DisposeAsync()` method is consistently called.
| Push vs. pull | Uses a "push" model where the callee sets the pace of updates. The client can only stop these updates by cancelling the original request. | Uses a "pull" model, so the server only calculates and sends data as the client needs it. The client can pause or even break out of enumeration at any time.
| Chattiness | Sends each report in its own message. | Supports batching values together.
| Server utilization | Server never waits for client before continuing its work. | Server only computes values when client asks for them, or when configured to "read ahead" for faster response times to client.

## Performance tuning

The `IAsyncEnumerator<T>` interface is defined assuming that every single value produced may be acquired asynchronously.
Thus an RPC implementation of this feature might be that every request for a value results in an RPC call to request
that value. Such a design would minimize memory consumption and cost of generating values unnecessarily,
but would be particularly noisy on the network and performance would suffer on high latency connections.

To improve performance across a network, this behavior can be modified:

1. **Batching**: When the consumer requests a value from the generator, the server may respond
   with the next several values in the same response message in order to reduce the number
   of round-trips the client must make while enumerating the sequence.
   This improves performance when network latency is significant.
1. **Read ahead**: The generator will do work to produce the next value(s) _before_ receiving
   the consumer's request for them. This allows for the possibility that the server is processing
   the data while the last value(s) are in transit to the client or being processed by the client.
   This improves performance when the time to generate the values is significant.

The above optimizations are configured individually and may be used in combination.

To accomplish this, we define these properties which tune the RPC bridge for async enumerables:

```cs
public class JsonRpcEnumerableSettings
{
    /// <summary>
    /// Gets or sets the maximum number of elements to read ahead and cache from the generator in anticipation of the consumer requesting those values.
    /// </summary>
    public int MaxReadAhead { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of elements to obtain from the generator before sending a batch of values to the consumer.
    /// </summary>
    public int MinBatchSize { get; set; } = 1;
}
```

The default values for these properties will result in the same behavior as one would observe
with `IAsyncEnumerable<T>` without any RPC: one value is produced or consumed at a time.
Individual services may choose based on expected use cases and performance costs to change these values
to improve the user experience.

To apply customized settings to an `IAsyncEnumerable<T>`, the generator should use our `WithJsonRpcSettings`
decorator extension method and provide the resulting value as the enumerable object.
For example given method `GenerateNumbersCoreAsync()` which returns an `IAsyncEnumerable<int>` object,
an RPC method can expose its result with custom RPC settings like this:

```cs
public IAsyncEnumerable<int> GenerateNumbersAsync(CancellationToken cancellationToken)
{
    return this.GenerateNumbersCoreAsync(cancellationToken)
        .WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = 10 });
}
```

A batch size of 10 with the default `MaxReadAhead` value of `0` means the server will not produce any values
until the client requests them, but when the client requests them, the client will get 10 or the rest of the
sequence, whichever is fewer.

Suppose `MaxReadAhead = 15` and `MinBatchSize = 10`. After the client calls `GenerateNumbersAsync` but before
it asks for the first element in the sequence, the server is already generating values till it fills a cache
of 15. When the client request comes in for the first value(s), the server will wait till at least 10 items have
 been produced before returning any to the client. If more than 10 were cached (e.g. the server was able to
 produce all 15 as part of "read ahead"), the client will get all that are cached. After the client's request
 is fulfilled, the read ahead server will continue generating items till the read ahead cache is full, in
 preparation for the next client request.

In short: `MinBatchSize` guarantees a minimum number of values the server will send to the client except where
the sequence is finished, and `MaxReadAhead` is how many values may be produced on the server in anticipation
of the next request from the client for more values.

The state machine and any cached values are released from the generator when the `IAsyncEnumerator<T>` is disposed.

Customized settings must be applied at the *generator* side. They are ignored if applied to the consumer side.
If the consumer is better positioned to determine the value of these settings, it may pass the values for
these settings to the generator for use in decorating the generator's object. For example, a server method
might be implemented like this:

```cs
public IAsyncEnumerable<int> GetNumbersAsync(int batchSize)
    => this.GetNumbersCoreAsync().WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = batchSize });
```

The above delegates to an C# iterator method, but decorates the result with a batch size determined by the client.

## Resource leaks concerns

The most important consideration is that of resource leaks. The party that transmits the `IAsyncEnumerable<T>`
has to reserve resources to respond to remote requests for more elements. This memory will be held until
the receiving party has called `IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken)` and
`IAsyncEnumerator<T>.DisposeAsync` on the result. When using C# 8 to `foreach` over the async enumerable,
this disposal pattern is guaranteed to happen. For example, this is the preferred usage pattern:

```cs
await foreach(int item in clientProxy.GetLongListAsync(cancellationToken))
{
    Console.WriteLine(item);
}
```

But take care if enumerating manually to ensure you set
up a `try/finally` block pattern that ensures it will be disposed. For example:

```cs
IAsyncEnumerable<int> enumerable = clientProxy.GetLongListAsync(cancellationToken);
IAsyncEnumerator<int> enumerator = enumerable.GetAsyncEnumerator(cancellationToken);
try
{
    while(await enumerator.MoveNextAsync())
    {
        Console.WriteLine(enumerator.Current);
    }
}
finally
{
    await enumerator.DisposeAsync();
}
```

When received as a result (or part of a result), the client *must* use `await foreach` or
manually call `IAsyncEnumerator<T>.DisposeAsync()` on the result to avoid a memory leak
on the server since the server has no way to know when the client is done using it.

When sent within an argument, any RPC-related resources for an `IAsyncEnumerable<T>` are
automatically released by the client when the server responds to the request
with an error or a result in case the server does not observe or enumerate the enumerable.

`IAsyncEnumerable<T>` may *not* be sent in notifications to avoid leaks when the server
does not handle the notification and send the disposal message.

All memory is automatically released when a JSON-RPC connection ends.
So worst case: if a memory leak is accumulating due to bad acting remote code,
closing the connection and allowing the `JsonRpc` instance to be collected will release
the memory associated with these enumerable tracking objects.

### Type mismatches

Another resource leak danger can occur when the server sends an `IAsyncEnumerable<T>` as a result
but the client is only expecting a `void` or `object` result. In that case the necessary client
proxy for `IAsyncEnumerable<T>` will not be produced, making disposal difficult or impossible and
leaving a leak on the server.

Always make sure the client is expecting the `IAsyncEnumerable<T>` when the server sends one as a result.

## Protocol

This section is primarily for JSON-RPC library authors that want to interop with StreamJsonRpc's
async enumerable feature.

### Originating message

A JSON-RPC message that carries an `IAsyncEnumerator<T>` provides a token to the enumerator
so that the message receiver can use this token to request values from or dispose the remote enumerator.
This token may be any valid JSON token except `null`:

A result that returns an `IAsyncEnumerable<T>` would look something like this:

```json
{
   "jsonrpc": "2.0",
   "id": 1,
   "result": "enum-handle"
}
```

A request that includes an `IAsyncEnumerable<T>` as a method argument might look like this:

```json
{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "FooAsync",
    "params": [
        "hi",
        "enum-handle"
    ]
}
```

An `IAsyncEnumerable<T>` might also appear as a property of an object that is included in the return value
or method argument:

```json
{
   "jsonrpc": "2.0",
   "id": 1,
   "result": {
       "enumerable": "enum-handle",
       "count": 10
   }
}
```

A client should not send an `IAsyncEnumerable<T>` object in a notification, since that would lead to
a memory leak on the client if the server does not handle a particular method or throws before it
could process the enumerable.

### Consumer request for values

A request from the consumer to the producer for (more) value(s) is done via a standard JSON-RPC
request method call with `$/enumerator/next` as the method name and one argument that carries
the enumerator token. When using named arguments this is named `token`.

```json
{
   "jsonrpc": "2.0",
   "id": 2,
   "method": "$/enumerator/next",
   "params": { "token": "enum-handle" }
}
```

or:

```json
{
   "jsonrpc": "2.0",
   "id": 2,
   "method": "$/enumerator/next",
   "params": [ "enum-handle" ]
}
```

The generator should respond to this request with an error containing `error.code = -32001`
when the specified enumeration token does not exist, possibly because it has already been disposed.

### Producer's response with values

A response with value(s) from the producer always comes as an object that contains an array of values
(even if only one element is provided) and a boolean indicating whether the last value has been provided:

```json
{
   "jsonrpc": "2.0",
   "id": 2,
   "result": {
      "values": [ 1, 2, 3 ],
      "finished": false
   }
}
```

Note the `finished` property is a hint from the producer for when the last value has been returned.
The server *may* not know that the last value has been returned and should specify `false` unless it is sure
the last value has been produced.

The client *may* ask for more values without regard to the `finished` property, but if `finished: true`
the client may optimize to not ask for more values but instead go directly to dispose of the enumerator.

The server should never return an empty array of values unless the last value in the sequence has already
been returned to the client.

### Consumer disposes enumerator

The consumer always notifies the producer when the local enumerator proxy is disposed
by invoking the `$/enumerator/dispose` method.
The arguments follow the same schema as the `$/enumerator/next` method.
This *may* be a notification.

```json
{
   "jsonrpc": "2.0",
   "method": "$/enumerator/dispose",
   "params": { "token": "enum-handle" },
}
```
