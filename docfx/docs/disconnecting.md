# Disconnecting

The JSON-RPC connection is disconnected for any of these scenarios:

1. The <xref:StreamJsonRpc.JsonRpc.Dispose?displayProperty=nameWithType> method is invoked.
1. The transport unexpectedly closes (e.g. lost network connection, remote process crashed, remote side closed the connection intentionally).
1. A message is received that does not parse to a valid JSON-RPC message, or some other protocol-level violation is detected.

When any of the above conditions are met, @StreamJsonRpc.JsonRpc will (in no particular order):

1. Call @System.IDisposable.Dispose on the @StreamJsonRpc.IJsonRpcMessageHandler, which is expected to dispose the underlying transport.
1. Faults all outstanding @System.Threading.Tasks.Task objects that represent outbound requests with a <xref:StreamJsonRpc.ConnectionLostException>.
1. If <xref:StreamJsonRpc.JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed?displayProperty=nameWithType> is `true`, cancel all <xref:System.Threading.CancellationToken>s that were provided as arguments to locally invoked methods.
1. Remove event handlers that may have been added to any target objects.
1. Completes the @System.Threading.Tasks.Task returned from the @StreamJsonRpc.JsonRpc.Completion?displayProperty=nameWithType property, either succesfully or with the exception that led to the connection termination.
1. Raise the <xref:StreamJsonRpc.JsonRpc.Disconnected?displayProperty=nameWithType> event, with an argument specifying the reason for the disconnection.

## Understanding the cause for an unexpected <xref:StreamJsonRpc.ConnectionLostException>

An unexpected disconnection may occur for any of the reasons given above.
Understanding *which* possible condition led to termination may require access to the logs and/or code running on both ends.

Given the <xref:StreamJsonRpc.JsonRpc.Disconnected?displayProperty=nameWithType> event includes the reason for disconnect, an event handler attached to this which writes any unexpected disconnect reasons to a file can be invaluable during an investigation.
It can be important to add the event handler to *both* sides of the connection since the root cause will typically be logged by only one side, while the other side may report a less helpful cause (e.g. the other side disconnected).

Following is a table of reasons that may be given by each party, and a plausible explanation for each one.
Party 1 is likely the first to notice and report a disconnect.

Party 1 | Party 2 | Explanation
--|--|--
<xref:StreamJsonRpc.DisconnectedReason.LocallyDisposed> | <xref:StreamJsonRpc.DisconnectedReason.RemotePartyTerminated> | Party 1 explicitly disconnected, and party 2 noticed.
<xref:StreamJsonRpc.DisconnectedReason.RemotePartyTerminated> | <xref:StreamJsonRpc.DisconnectedReason.RemotePartyTerminated> | The transport failed. May be common across unreliable networks.
<xref:StreamJsonRpc.DisconnectedReason.StreamError> | <xref:StreamJsonRpc.DisconnectedReason.RemotePartyTerminated> or <xref:StreamJsonRpc.DisconnectedReason.StreamError> | Party 1 experienced some I/O error that made reading from the stream impossible. Party 2 may experience the same, or notice when Party 1 disconnects as a result of the first error.
<xref:StreamJsonRpc.DisconnectedReason.LocalContractViolation> | <xref:StreamJsonRpc.DisconnectedReason.RemotePartyTerminated> | Party 1 knew it would be unable to maintain its protocol contract (e.g. a critical serialization failure or misbehaving extension) and killed its own connection.
<xref:StreamJsonRpc.DisconnectedReason.RemoteProtocolViolation>, <xref:StreamJsonRpc.DisconnectedReason.ParseError> | * | Party 1 recognized that Party 2 sent a message that violated the protocol, so party 1 disconnected.

In addition to the <xref:StreamJsonRpc.JsonRpcDisconnectedEventArgs.Reason?displayProperty=nameWithType> given to a handler of the <xref:StreamJsonRpc.JsonRpc.Disconnected?displayProperty=nameWithType> event, the <xref:StreamJsonRpc.JsonRpcDisconnectedEventArgs> class includes several other properties that can be very helpful in understanding details of a failure.
Logging the whole object can be a good idea.

### Using TraceSource for logging

Most of the properties available on this object are also traced out via <xref:StreamJsonRpc.JsonRpc.TraceSource?displayProperty=nameWithType> on disconnect.
Adding a <xref:System.Diagnostics.TraceListener> to that <xref:System.Diagnostics.TraceSource> is usually adequate for logging and captures many other warning and error conditions that may occur without a disconnect.

<xref:StreamJsonRpc.DisconnectedReason.LocallyDisposed> and <xref:StreamJsonRpc.DisconnectedReason.RemotePartyTerminated> disconnection reasons are considered normal and are traced with <xref:System.Diagnostics.TraceEventType.Information?displayProperty=nameWithType> severity level.
All other reasons are traced with <xref:System.Diagnostics.TraceEventType.Critical?displayProperty=nameWithType> severity level.
