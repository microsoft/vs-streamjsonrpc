# Exotic Types

Some types are not serializable but are specially recognized and marshaled by StreamJsonRpc when used in arguments or return values.

* [`CancellationToken`](../docs/sendrequest.md#cancellation)
* [`IProgress<T>`](progresssupport.md)
* [`Stream`, `IDuplexPipe`, `PipeReader`, `PipeWriter`](oob_streams.md)
* [`IAsyncEnumerable<T>`](asyncenumerable.md)
* [`IObserver<T>`](observer.md)
* [`IDisposable`](disposable.md)
* [Interfaces marked with `RpcMarshalableAttribute`](rpc_marshalable_objects.md)
* [General marshalable object support](general_marshaled_objects.md)

The @System.Threading.CancellationToken support is built into the @StreamJsonRpc.JsonRpc class itself so that it works in any configuration, provided the remote side also supports it.

The rest of the types listed above require support by the <xref:StreamJsonRpc.IJsonRpcMessageFormatter> in use. All formatters that ship within the StreamJsonRpc library come with built-in support for all of these.
