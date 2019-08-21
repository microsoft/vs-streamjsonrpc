# IProgress<T> support

Support for IProgress<T> allows a JSON-RPC server to report progress to the client periodically so that the client can start processing those 
results without having to wait for all results to be retrieved before responding.

## Client side:

With this support client can now include an object that implements IProgress<T> on the invocation parameters. The Report method on that 
instance should have the processing that the client will do with the results reported by the server.

Since IProgress<T> is not serializable to JSON, the formatter of the JsonRpc instance will store the object implementing IProgress<T> in a
dictionary and replace it with a token in the JSON message before sending it to the server. It will also store this token along with the 
request's id in another dictionary so that we can associate a request id with its IProgress<T> instance.

The request sent to the server will look like this:

`
{
  "jsonrpc": "2.0",
  "id": 153,
  "method": "someMethodName",
  "params": {
    "units": 5,
    "progress": "some-JSON-token"
  }
}
`

The progress argument may have any name or position, and may have any valid JSON token as its value. A value of null is specially recognized as 
an indication that the client does not want progress updates from the server.

Reports from the server will come in form of special notifications ($/progress), which will include the token that replaced the object 
implementing IProgress<T> and the values to report. Whenever the client receives this kind of notifications, it will take the given token to
retrieve the IProgress<T> instance from the dictionary and use it to Report the values obtained by the server.

This reporting will continue until the client receives the actual response to the request. When the response is received the client will use 
the request id to obtain the generated token for the IProgress<T> instance and then use that token to remove the object from the dictionary to 
avoid memory leaks.

## Server side:

When the method called on the server supports IProgress<T>, the server's will receive a token from the client instead of an actual IProgress<T>
instance. Since the server method will require an object implementing IProgress<T> to report the results, the server's formatter will take the
token given by the client and create such object.

This new IProgress<T> instance will send a special kind of notification ($/progress) whenever results are reported on the server method, using 
the token given by the client and the reported values as parameters.

The progress notification will look like this:

`
{
  "jsonrpc": "2.0",
  "method": "$/progress",
  "params": {
    "token": "some-JSON-token",
    "value": { "some": "status-token" }
  }
}
`

## Links:
[Spec proposal](https://github.com/microsoft/vs-streamjsonrpc/issues/139)
[Issue documenting the protocol](https://github.com/microsoft/language-server-protocol/issues/786)


