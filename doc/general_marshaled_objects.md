# General marshaled objects

Passing objects over RPC by value is the default and preferred means of exchanging information.
Objects passed by value have no relationship between sender and receiver.
Any methods defined on the object execute locally in the process on which they were executed and do not result in an RPC call.
Objects passed by value are *serialized* by the sender with all their member data and deserialized by the receiver.

When objects do not have a serialized representation (e.g. they contain no data) but do expose methods that should be invokable by a remote party in an RPC connection, that object may be _marshaled_ instead of serialized.
When an object is marshaled, a handle to that object is transmitted that allows the receiver to direct RPC calls to that particular object.
A marshaled object's lifetime is connected between sender and receiver, and will only be released when either sender or receiver dispose the handle or the overall RPC connection.

To the receiver, a marshaled object is represented by an instance of the marshaled interface.
The implementation of that interface is an RPC proxy that is *very* similar to ordinary [dynamic proxies](dynamicproxy.md) that may be generated for the primary RPC connection.
This interface has the same restrictions as documented for these dynamic proxies.

**CONSIDER**: Will interfaces with events defined on them behave as expected, or should we disallow events on marshaled interfaces?

Marshaled objects may _not_ be sent in arguments passed in a notification.
An attempt to do so will result in an exception being thrown at the client instead of transmitting the message.
This is because notification senders have no guarantee the server accepted and processed the message successfully, making lifetime control of the marshaled object impossible.

## Use cases

When preparing an object to be marshaled, only methods defined on the given interface are exposed for invocation by RPC.
Other methods on the target object cannot be invoked.

Every marshaled object's proxy implements `IDisposable`.
Invoking `IDisposable.Dispose` on a proxy transmits a `dispose` RPC notification to the target object and releases the proxy.

See [additional use cases being considered](general_marshaled_objects_2.md) for general marshalling support.

## Protocol

Any marshaled object is encoded as a JSON object with the following properties:

- `__jsonrpc_marshaled` - required
- `handle` - required
- `lifetime` - optional

The `__jsonrpc_marshaled` property is a number that may be one of the following values:

Value | Explanation
--|--
`1` | Used when a real object is being marshaled. The receiver should generate a new proxy that directs all RPC requests back to the sender referencing the value of the `handle` property.
`0` | Used when a marshaled proxy is being sent *back* to its owner. The owner uses the `handle` property to look up the original object and use it as the provided value.

The `handle` property is a signed 64-bit number.
It SHOULD be unique within the scope and duration of the entire JSON-RPC connection.
A single object is assigned a new handle each time it gets marshaled and each handle's lifetime is distinct.

The `lifetime` property is a string that MAY be included and set to one of the following values:

Value | Explanation
--|--
`"call"` | The marshaled object may only be invoked until the containing RPC call completes. This value is only allowed when used within a JSON-RPC argument. No explicit release using `$/releaseMarshaledObject` is required.
`"explicit"` | The marshaled object may be invoked until `$/releaseMarshaledObject` releases it. **This is the default behavior when the `lifetime` property is omitted.**

### Marshaling an object

Consider this example where `SomeMethod(int a, ISomething b, int c)` is invoked with a marshaled object for the second parameter:

```json
{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "SomeMethod",
    "params": [
        1,
        { "__jsonrpc_marshaled": 1, "handle": 5 },
        3,
    ]
}
```

If the RPC server returns a JSON-RPC error response (for any reason), all objects marshalled in the arguments of the request are released immediately to mitigate memory leaks since the server cannot be expected to have recognized the arguments as requiring a special release notification to be sent from the server back to the client.

### Invoking a method on a marshaled object

The receiver of the above request can invoke `DoSomething` on the marshaled `ISomething` object with a request such as this:

```json
{
    "jsonrpc": "2.0",
    "id": 15,
    "method": "$/invokeProxy/5/DoSomething",
    "params": []
}
```

### Referencing a marshaled object

The receiver of the marshaled object may also "reference" the marshaled object by using `__jsonrpc_marshaled: 0`,
as in this example:

```json
{
    "jsonrpc": "2.0",
    "id": 16,
    "method": "RememberWhenYouSentMe",
    "params": [
        1,
        { "__jsonrpc_marshaled": 0, "handle": 5 },
        3,
    ]
}
```

### Releasing marshaled objects

Either side may terminate the marshalled connection in order to release resources by sending a notification to the `$/releaseMarshaledObject` method.
The `handle` named parameter (or first positional parameter) is set to the handle of the marshaled object to be released.
The `ownedBySender` named parameter (or second positional parameter) is set to a boolean value indicating whether the party sending this notification is also the party that sent the marshaled object.

```json
{
    "jsonrpc": 20,
    "method": "$/releaseMarshaledObject",
    "params": {
        "handle": 5,
        "ownedBySender": true,
    }
}
```

One or both parties may send the above notification.
If the notification is received after having sent one relating to the same handle, the notification may be discarded.

### Referencing of marshaled objects after release

When an RPC _request_ references a released marshaled object, the RPC server SHOULD respond with a JSON-RPC error message containing `error.code = -32001`.

When an RPC _result_ references a released marshaled object, the RPC client library MAY throw an exception to its local caller.

### No nesting of marshaled object lifetimes

Although object B may be marshaled in RPC messages that target marshaled object B, marshaled object B's lifetime is _not_ nested within A's.
Each marshaled object must be indepenently released.
