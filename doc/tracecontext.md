# Trace Context

This document describes how StreamJsonRpc propagates [Trace Context][trace-context] over JSON-RPC.

StreamJsonRpc propagates trace context only in JSON-RPC requests (including notifications).
Trace context does not propagate over response messages.

## Usage

StreamJsonRpc participates in and propagates trace context when the `JsonRpc.ActivityTracingStrategy` property is set to an implementation of the `IActivityTracingStrategy` interface.
StreamJsonRpc includes two such implementations, as described in the following sections.

### `ActivityTracingStrategy`

This strategy utilizes the [`System.Diagnostics.Activity` API](https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.activity?view=netcore-3.1) from the [`System.Diagnostics.DiagnosticSource` NuGet package](https://www.nuget.org/packages/System.Diagnostics.DiagnosticSource) to track activities.

No trace context is applied to outbound requests when `Activity.Current is null`
or when `Activity.Current.IdFormat != ActivityIdFormat.W3C`.

The value of the `Activity.Current.Id` property is used directly for the `traceparent` property in an outbound RPC request.

The value of the `Activity.Current.TraceStateString` property is used directly for the `tracestate` property in an outbound RPC request.

On an inbound request that carries trace context data, a new `Activity` is started whose `ParentId` is set to the value of the request's `traceparent` property and whose `TraceStateString` property is set to the value of the request's `tracestate` property.
The `Activity.Name` is set to the name of the method being invoked via RPC.

### `CorrelationManagerTracingStrategy`

This strategy utilizes the `Trace.CorrelationManager` and `TraceSource` APIs to track activities.

No trace context is applied to outbound requests when `Trace.CorrelationManager.ActivityId == Guid.Empty`.

This strategy populates the `traceparent` property as follows:

sub-field     | value or source
--------------|-----------------------------------------------------------------------
`version`     | `0`
`trace-id`    | [`Trace.CorrelationManager.ActivityId`][CorrelationManagerActivityId]
`parent-id`   | a random value for each outbound request
`trace-flags` | `sampled` if `CorrelationManagerTracingStrategy.TraceSource` is set to an instance with at least one `TraceListener` and the `SourceLevels.ActivityTracing` flag set on its `TraceSource.Switch` property.

When receiving requests, a new `Guid` is assigned to the `Trace.CorrelationManager.ActivityId` property before dispatching the request.
The `trace-id` from the request is recorded as a parent to this new activity through a call to `TraceSource.TraceTransfer`.
Any prior value for the `ActivityId` property is preserved and reapplied after the request has been handled.
When an activity is applied or reverted, the appropriate `TraceSource` APIs are called (e.g. `TraceTransfer`, `TraceEvent` with `TraceEventType.Start` or `TraceEventType.Stop`).

The `tracestate` property on an outbound request is set to the value of `CorrelationManagerTracingStrategy.TraceState`.
For an inbound request, the value from the request is applied to this same property.

Tracing using the `TraceSource` API will include the value from `Trace.CorrelationManager.ActivityId` in each traced message if the [SourceLevels.ActivityTracing][SourceLevelsActivityTracingFlag] flag is set.
Note that this flag *not* included in `SourceLevels.Verbose`.

For example, to construct a `TraceSource` that will emit activity IDs for correlation between processes and machines, use code such as:

```cs
var traceSource = new TraceSource("some name", SourceLevels.Warning | SourceLevels.ActivityTracing);
```

Or to modify an existing `TraceSource` to add activity tracing, you can set the flag:

```cs
traceSource.Switch.Level |= SourceLevels.ActivityTracing;
```

You *may* share a `TraceSource` or `TraceListener` between your `JsonRpc` instance and the `CorrelationManagerTracingStrategy` instance.
For proper activity recording in trace logs, be sure to set the `SourceLevels.ActivityTracing` flag described above in all relevant `TraceSource` objects.

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

- `traceparent` as `[uint8, [uint8[], uint8[], uint8]]`
- `tracestate` is `[str, str, str, str]` (an array with an even numbered length, where the odd numbered elements are keys and even numbered elements are values associated with the keys that immediately preceded them.)

[trace-context]: https://www.w3.org/TR/trace-context/
[traceparent]: https://www.w3.org/TR/trace-context/#traceparent-header-field-values
[tracestate]: https://www.w3.org/TR/trace-context/#tracestate-header-field-values
[CorrelationManagerActivityId]: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.correlationmanager.activityid?view=netcore-3.1
[SourceLevelsActivityTracingFlag]: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.sourcelevels?view=netcore-3.1#System_Diagnostics_SourceLevels_ActivityTracing
[XmlWriterTraceListener]: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.xmlwritertracelistener?view=netcore-3.1
[ServiceTraceViewer]: https://docs.microsoft.com/en-us/dotnet/framework/wcf/service-trace-viewer-tool-svctraceviewer-exe#using-the-service-trace-viewer-tool
