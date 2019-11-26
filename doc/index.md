# StreamJsonRpc Documentation

If you're new to JSON-RPC and/or StreamJsonRpc, check out our brief [overview](overview.md).
The rest of our documentation are organized around use case.

## Use cases

1. [Establish a connection](connecting.md)
1. [Send an RPC request](sendrequest.md)
1. [Receive an RPC request](recvrequest.md)
1. [Disconnect](disconnecting.md)
1. [Test your code](testing.md)
1. [Write resilient code](resiliency.md)
1. [Remote Targets](remotetargets.md)
1. Passing around
   1. [`Stream`/`IDuplexPipe`](oob_streams.md)
   1. [`IProgress<T>`](progresssupport.md)
   1. [`IAsyncEnumerable<T>`](asyncenumerable.md)
1. [Troubleshoot](troubleshooting.md)

See [full samples](https://github.com/AArnott/StreamJsonRpc.Sample) demonstrating two processes
on the same machine utilizing this library for RPC, using either named pipes or web sockets.

[Learn more about how you can customize the JSON-RPC protocol's wire format](extensibility.md) that StreamJsonRpc uses for better interoperability or better performance.

See also [Visual Studio specific concerns](vs.md).
