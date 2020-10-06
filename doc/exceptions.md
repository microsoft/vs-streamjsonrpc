# Throwing and handling exceptions

The JSON-RPC protocol allows for server methods to return errors to the client instead of a result, except when the client invoked the method as a notification.

As a cross-platform protocol, JSON-RPC doesn't define error messages with enough precision to represent .NET exceptions, callstacks, etc. with full fidelity.
An exception thrown from a .NET-based JSON-RPC server may contain data that will not be included in the JSON-RPC error message that is sent back to the client.
The data that is sent back may only be human readable rather than machine parsable.

The [structure JSON-RPC defines for errors](https://www.jsonrpc.org/specification#response_object) includes an error code and a message.

Error codes -32768 to -32000 are reserved for the protocol itself or for the library that implements it.
The rest of the 32-bit integer range of the error code is available for the application to define.
This error code is the best way for an RPC server to communicate a particular kind of error that the RPC client may use for controlling execution flow. For example the server may use an error code to indicate a conflict and another code to indicate a permission denied error. The client may check this error code and branch execution based on its value.

The error *message* should be a localized, human readable message that explains the problem, possibly to the programmer of the RPC client or perhaps to the end user of the application.

JSON-RPC also allows for an `error.data` property which may be a primitive value, array or object that provides more data regarding the error.
The schema for this property is up to the application, but StreamJsonRpc defaults to serializing
a `CommonErrorData` object to this property which retains much of the useful information from an
`Exception` object.

By setting `JsonRpc.ExceptionStrategy` to `ExceptionProcessing.ISerializable`, `JsonRpc` will use the `ISerializable` patterns in .NET to serialize exceptions with higher fidelity. On the RPC client the `RemoteInvocationException` will have its `InnerException` property set with the original exception (and inner exceptions) thrown at the RPC server.
This requires that the exception strategy be set both on the server and on the client.

## Server-side concerns

In StreamJsonRpc, your RPC server can return errors to the client by throwing an exception from your RPC method. StreamJsonRpc will automatically serialize data from the exception as an error response and transmit to the client when allowed. If the RPC method was invoked using a JSON-RPC notification, the client is not expecting any response and the exception thrown from the server will be swallowed.

If some or all exceptions thrown from RPC methods should be considered fatal and terminate the JSON-RPC connection, you can configure this behavior. See the [Fatal exceptions section of our resiliency doc](resiliency.md#Fatal-exceptions).

By default any exception thrown from an RPC method is assigned `JsonRpcErrorCode.InvocationError` (-32000) for the JSON-RPC `error.code` property. The `Exception.Message` property is used as the JSON-RPC `error.message` property.
When `ExceptionProcessing.ISerializable` is used the error code `JsonRpcErrorCode.InvocationErrorWithException` (-32004) indicates when full `Exception` data is serialized instead of just that data captured by `CommonErrorData`.

An RPC server may take total control of `error.message` value simply by throwing any exception type with the message to use.
The RPC server may also take control of the `error.code` and `error.data` properties by throwing `LocalRpcException`, which has properties for each of these JSON-RPC error message properties.
When a JSON-RPC server may throw multiple exceptions that clients should react differently to, it's highly recommended that the RPC server throw `LocalRpcException` and set its `ErrorCode` property so the client can check and branch based on it.

Another option to more closely control the error messages sent from an RPC server is to derive from the `JsonRpc` class and override its `CreateErrorDetails` method.
This allows the server to inspect the original JSON-RPC request as well as the exception thrown from the RPC server method and determine whatever `error.code`, `error.message` and `error.data` the JSON-RPC client requires.

## Client-side concerns

An invocation of an RPC method may throw several exceptions back at the client.
A full list of these exceptions are documented on each Invoke or Notify method API on the `JsonRpc` class.
All these exceptions may also be thrown from methods on dynamically generated proxies.
Relevant to the discussion of handling exceptions thrown from the RPC server are these exceptions which the client should be prepared to handle:

1. `OperationCanceledException` - Thrown when the client cancels the request before it is transmitted or when the server acknowledged a cancellation notification and terminated the RPC method at the server.
1. `RemoteInvocationException` - Thrown when the server responds to a request with an error message other than one that acknowledges cancellation.

The `RemoteInvocationException` contains properties to inspect the `error.code`, `error.message` and `error.data` properties from the original JSON-RPC error message.

Because `error.data` may have any schema, extracting this property from `RemoteInvocationException` may require a bit more effort. The exception's `DeserializedErrorData` property is typically the best one to use and will typically be an instance of `CommonErrorData` which you can try casting to. `CommonErrorData` contains most of the details useful from a .NET Exception.

When working with a JSON-RPC server that responds with `error.data` that does *not* conform to the `CommonErrorData` type defined by StreamJsonRpc, you may derive from the `JsonRpc` class and override the `GetErrorDetailsDataType` method to inspect the JSON-RPC error message directly and return the type of data object that should be deserialized for easier consumption from your exception handlers.

By default, the `RemoteInvocationException.InnerException` property does *not* contain a deserialized exception tree from what was thrown on the server. The server may not even be based on .NET, but even if it were, .NET exceptions are not serialized such that they can be deserialized over a JSON-RPC connection. Thus this property will typically be `null` and should not generally be relied on.
When `ExceptionProcessing.ISerializable` is used there *will* be an `InnerException` which is the original one thrown from the server if the server provided the data to deserialize.
