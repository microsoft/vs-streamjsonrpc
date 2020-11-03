# Trace Context

This document describes how StreamJsonRpc propagates [Trace Context][trace-context] over JSON-RPC.

StreamJsonRpc propagates trace context only in JSON-RPC requests (including notifications).

When sending requests, StreamJsonRpc takes the trace context from the [`Trace.CorrelationManager.ActivityId` property][ActivityId].
A new activity is created and parented to that existing `ActivityId` and the new activity's ID is then sent to the remote party.

When receiving requests, StreamJsonRpc sets the `ActivityId` property before dispatching the request.

## Usage

StreamJsonRpc participates in and propagates trace context when the `JsonRpc.IsTraceContextEnabled` property is set to `true`.

### Include activity tracing information in `TraceSource`

Tracing using the `TraceSource` API will include the `ActivityId` in each traced message if the [SourceLevels.ActivityTracing][ActivityTracingFlag] flag is set.
For example, to construct a `TraceSource` that will emit activity IDs for correlation between processes and machines, use code such as:

```cs
var traceSource = new TraceSource("some name", SourceLevels.Warning | SourceLevels.ActivityTracing);
```

Or to modify an existing `TraceSource` to add activity tracing, you can set the flag:

```cs
traceSource.Switch.Level |= SourceLevels.ActivityTracing;
```

### Creating and reviewing trace logs

Reviewing trace files is much easier, particularly when reviewing many files that may span processes and/or machines, when using a tool such as [Service Trace Viewer][ServiceTraceViewer].
This viewer reads trace files written with the [`XmlWriterTraceListener`](XmlWriterTraceListener).
Add an instance of this class to your `TraceSource.Listeners` collection for the best trace log viewing experience.
This can be used in combination with other unstructured `TraceListener`-derived classes if desired.

## Protocol

The formatting, parsing, propagating and modification rules all apply as defined in the [Trace Context][trace-context] spec.

The `tracestate` property is optional and MAY be omitted when empty.
The `traceparent` property is optional but MUST be present if `tracestate` is present.

### Text encoding

When using a text-based encoding (e.g. UTF-8) the trace-context values are encoded as strings as follows:

Given a standard JSON-RPC request:

```json
{
  "jsonrpc": "2.0",
  "method": "pick",
  "params": [],
  "id": 1
}
```

We include the trace context by adding a property to the JSON-RPC message for each of the HTTP headers described in the [Trace Context W3C spec][trace-context]: [`traceparent`][traceparent] and [`tracestate`][tracestate].

For example, a JSON-RPC request message may look like this:

```json
{
  "jsonrpc": "2.0",
  "method": "pick",
  "params": [],
  "id": 1,
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "tracestate": "rojo=00f067aa0ba902b7,congo=t61rcWkgMzE"
}
```

### Binary encoding

When using a binary encoding (e.g. MessagePack) the trace-context values are encoded as arrays of binary elements as follows:

- `traceparent` is an array containing an 8-byte unsigned integer, a byte array, a byte array, and another unsigned integer.
- `tracestate` is an array with an even numbered length, where the odd numbered elements are keys and even numbered elements are values associated with the keys that immediately preceded them.

[trace-context]: https://www.w3.org/TR/trace-context/
[traceparent]: https://www.w3.org/TR/trace-context/#traceparent-header-field-values
[tracestate]: https://www.w3.org/TR/trace-context/#tracestate-header-field-values
[ActivityId]: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.correlationmanager.activityid?view=netcore-3.1
[ActivityTracingFlag]: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.sourcelevels?view=netcore-3.1#System_Diagnostics_SourceLevels_ActivityTracing
[XmlWriterTraceListener]: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.xmlwritertracelistener?view=netcore-3.1
[ServiceTraceViewer]: https://docs.microsoft.com/en-us/dotnet/framework/wcf/service-trace-viewer-tool-svctraceviewer-exe#using-the-service-trace-viewer-tool
