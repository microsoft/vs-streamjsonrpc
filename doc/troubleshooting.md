# Troubleshooting

When investigating failures, you may find StreamJsonRpc's tracing functionality useful.

With the `JsonRpc.TraceSource` property, you can listen for:

1. Which server methods are registered
1. Incoming and outgoing JSON-RPC messages and how they're being handled along the entire pipeline
1. When listening is started
1. RPC method invocation failures with full exception callstacks.

The above is just a sample. The full list of events is available on the `JsonRpc.TraceEvents` enum.
