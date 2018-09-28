# Disconnecting

The JSON-RPC connection is disconnected for any of these scenarios:

1. The `JsonRpc.Dispose` method is invoked.
1. The transport unexpectedly closes.
1. A message is received that does not parse to a valid JSON-RPC message, or some other protocol-level violation is detected.

When any of the above conditions are met, `JsonRpc` will (in no particular order):

1. Call `Dispose` on the `IJsonRpcMessageHandler`, which is expected to dispose the underlying transport.
1. Faults all outstanding `Task` objects that represent outbound requests with a `ConnectionLostException`.
1. If `CancelLocallyInvokedMethodsWhenConnectionIsClosed` is `true`, cancel all `CancellationToken`s that were provided as arguments to locally invoked methods.
1. Remove event handlers that may have been added to any target objects.
1. Completes the `Task` returned from the `JsonRpc.Completion` property, either succesfully or with the exception that led to the connection termination.
1. Raise the `JsonRpc.Disconnected` event, with an argument specifying the reason for the disconnection.
