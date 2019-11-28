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
await foreach (int number in this.clientProxy.GenerateNumbersAsync(token).WithCancellation(token))
{
    Console.WriteLine(number);
}
```

All the foregoing is simple C# 8 async enumerable syntax and use cases.
StreamJsonRpc lets you use this natural syntax over an RPC connection.

We pass `token` in once to the method that calls to the RPC server, and again to the `WithCancellation`
extension method so that the token is applied to each iteration of the loop over the enumerable.

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
1. **Prefetch**: The generator collects some number of values up front and _includes_ them
   in the initial message with the token for acquiring more values.
   While "read ahead" reduces the time the consumer must wait while the generator produces the values
   for each request, this prefetch setting entirely eliminates the latency of a round-trip for just
   the first set of items.

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

As the prefetch feature requires an asynchronous operation itself to fill a cache of items for transmission
to the receiver, it is not merely a setting but a separate extension method: `WithPrefetchAsync`.
It can be composed with `WithJsonRpcSettings` in either order, but it's syntactically simplest to add last
since it is an async method:

```cs
public async ValueTask<IAsyncEnumerable<int>> GenerateNumbersAsync(CancellationToken cancellationToken)
{
    return await this.GenerateNumbersCoreAsync(cancellationToken)
        .WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = 10 })
        .WithPrefetchAsync(count: 20, cancellationToken);
}
```

Please refer to the section on RPC interfaces below for a discussion on the return type of the above method.

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

## RPC interfaces

When an async iterator method can be written to return `IAsyncEnumerator<T>` directly,
it makes for a natural implementation of the ideal C# interface, such as:

```cs
interface IService
{
    IAsyncEnumerable<int> GetNumbersAsync(CancellationToken cancellationToken);
}
```

This often can be implemented as simply as:

```cs
public async IAsyncEnumerable<int> GetNumbersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
{
    for (int i = 1; i <= 20; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return i;
    }
}
```

But when applying the perf modifiers, additional steps must be taken:

1. Rename the C# iterator method and (optionally) make it private.
1. Expose a new implementation of the interface method which calls the inner one and applies the modifications.

```cs
public IAsyncEnumerable<int> GetNumbersAsync(CancellationToken cancellationToken)
{
    return this.GetNumbersCoreAsync(cancellationToken)
        .WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = batchSize });
}

private async IAsyncEnumerable<int> GetNumbersCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
{
    for (int i = 1; i <= 20; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return i;
    }
}
```

The above isn't too inconvenient, but it is a bit of extra work. It still can implement the same interface that is shared with the client.
But it gets more complicated when adding `WithPrefetchAsync`, since now the wrapper method itself contains an `await`.
It cannot simply return `IAsyncEnumerable<int>` since it is not using `yield return` but rather it's returning a specially-decorated
`IAsyncEnumerable<T>` instance.
So instead, we have to use `async ValueTask<IAsyncEnumerable<T>>` as the return type:

```cs
public async ValueTask<IAsyncEnumerable<int>> GetNumbersAsync(CancellationToken cancellationToken)
{
    return await this.GetNumbersCoreAsync(cancellationToken)
        .WithPrefetchAsync(10);
}

private async IAsyncEnumerable<int> GetNumbersCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
{
    for (int i = 1; i <= 20; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return i;
    }
}
```

But now we've changed the method signature of the primary method that JSON-RPC will invoke and we can't implement the C# interface
as it was originally. We can change the interface to also return `ValueTask<IAsyncEnumerable<int>>` but then that changes
the client's calling pattern to add an extra *await* (the one after `in`):

```cs
await foreach(var item in await serverProxy.GetNumbersAsync(cancellationToken))
{
    //
}
```

That's a little more awkward to write. If we want to preserve the original consuming syntax, we can:

1. Keep the interface as simply returning `IAsyncEnumerable<T>`.
1. Stop implementing the interface on the server class itself.
1. Change the server method to return `async ValueTask<IAsyncEnumerable<int>>` as required for `WithPrefetchAsync`.

This is a trade-off between convenience in maintaining and verifying the JSON-RPC server actually satisfies
the required interface vs. the client writing the most natural C# `await foreach` code.

Note that while the proxy generator requires that _all_ methods return either `Task{<T>}`, `ValueTask{<T>}` or
`IAsyncEnumerable<T>`, the server class itself can return any type, and StreamJsonRpc will ultimately
make it appear to the client to be asynchronous. For example a server method can return `int` while the client
proxy interface is written as if the server method returned `Task<int>` and it will work just fine.
This is why the interface method can return `IAsyncEnumerable<int>` while the server class method can actually
return `ValueTask<IAsyncEnumerable<int>>` without there being a problem.

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

An `IAsyncEnumerable<T>` object may be included within or as an RPC method argument or return value.
We use the terms `generator` to refer to the sender and `consumer` to refer to the receiver.

Capitalized words are key words per [RFC 2119](https://tools.ietf.org/html/rfc2119).

### Originating message

A JSON-RPC message that carries an `IAsyncEnumerator<T>` encodes it as a JSON object.
The JSON object may contain these properties:

| property | description |
|--|--|
| `token` | Any valid JSON token except `null` to be used to request additional values or dispose of the enumerator. This property is **required** if there are more values than those included in this message, and must be absent or `null` if all values are included in the message.
| `values` | A JSON array of the first batch of values. This property is **optional** or may be specified as `null` or an empty array. A lack of values here does not signify the enumerable is empty but rather that the consumer must explicitly request them.

A result that returns an `IAsyncEnumerable<T>` would look something like this if it included the first few items and more might be available should the receiver ask for them:

```json
{
   "jsonrpc": "2.0",
   "id": 1,
   "result": {
     "token": "enum-handle",
     "values": [ 1, 2, 3 ]
   }
}
```

A request that includes an `IAsyncEnumerable<T>` as a method argument might look like this if it included the first few items and more might be available should the receiver ask for them:

```json
{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "FooAsync",
    "params": [
        "hi",
        {
          "token": "enum-handle",
          "values": [ 1, 2, 3 ]
        }
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
       "enumerable": {
         "token": "enum-handle",
         "values": [ 1, 2, 3 ]
       },
       "count": 10
   }
}
```

The enumerable certainly may include no pre-fetched values. This object (which may appear in any of the above contexts) demonstrates this:

```json
{
  "token": "enum-handle"
}
```

The inclusion of the `token` property signifies that the receiver should query for more values or dispose of the enumerable.

Alternatively if the prefetched values are known to include *all* values such that the receiver need not ask for more,
we would have just the other property:

```json
{
  "values": [ 1, 2, 3 ]
}
```

Finally, if the enumerable is known to be empty, the object may be completely empty:

```json
{
}
```

A client SHOULD NOT send an `IAsyncEnumerable<T>` object in a notification, since that would lead to
a memory leak on the client if the server does not handle a particular method or throws before it
could process the enumerable.

The generator MAY pass multiple `IAsyncEnumerable<T>` instances in a single JSON-RPC message.

### Consumer request for values

A request from the consumer to the generator for (more) value(s) is done via a standard JSON-RPC
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

The consumer MUST NOT send this message after receiving a message related to this enumerable
with `finished: true` in it.
The consumer MUST NOT send this message for a given enumerable
while waiting for a response to a previous request for the same enumerable,
since the generator may respond to an earlier request with `finished: true`.

The consumer MAY cancel a request using the `$/cancelRequest` method as described elsewhere.
The consumer MUST continue the enumeration or dispose it if the server responds with a
result rather than a cancellation error.

The generator SHOULD respond to this request with an error containing `error.code = -32001`
when the specified enumeration token does not exist, possibly because it has already been disposed
or because the last set of values provided to the consumer included `finished: true`.

### Generator's response with values

A response with value(s) from the generator is encoded as a JSON object.
The JSON object may contain these properties:

| property | description |
|--|--|
| values | A JSON array of values. This value is **required.**
| finished | A boolean value indicating whether the last value from the enumerable has been returned. This value is **optional** and defaults to `false`.

Here is an example of a result encoded as a JSON object:

```json
{
   "jsonrpc": "2.0",
   "id": 2,
   "result": {
      "values": [ 4, 5, 6 ],
      "finished": false
   }
}
```

The server MUST specify `finished: true` only when it is sure the last value in the enumerable has been returned.
The server SHOULD release all resources related to the enumerable and token when doing so.

The server MAY specify `finished: false` in one response and `values: [], finished: true` in the next response.

The consumer MUST NOT ask for more values when `finished` is `true` or an error response is received.
The generator MAY respond with an error if this is done.

The generator should never return an empty array of values unless the last value in the sequence has already
been returned to the client.

### Consumer disposes enumerator

When the consumer aborts enumeration before the generator has sent `finished: true`,
the consumer MUST send a disposal message to release resources held by the generator
unless the generator has already responded with an error message to a previous request for values.

The consumer does this by invoking the `$/enumerator/abort` JSON-RPC method on the generator.
The arguments follow the same schema as the `$/enumerator/next` method.
This MAY be a notification.

```json
{
   "jsonrpc": "2.0",
   "method": "$/enumerator/abort",
   "params": { "token": "enum-handle" },
}
```

The generator SHOULD release resources upon receipt of the disposal message.
The generator SHOULD reject any disposal request received after sending a `finished: true` message.
