# Getting Started

## Installation

Consume this library via its NuGet Package.
Click on the badge to find its latest version and the instructions for consuming it that best apply to your project.

[![NuGet package](https://img.shields.io/nuget/v/StreamJsonRpc.svg)](https://nuget.org/packages/StreamJsonRpc)

## Use cases

If you're new to JSON-RPC and/or StreamJsonRpc, check out our brief [overview](../index.md).
The rest of our documentation are organized around use case.

1. [Establish a connection](connecting.md)
1. [Send an RPC request](sendrequest.md)
1. [Receive an RPC request](recvrequest.md)
1. [Disconnect](disconnecting.md)
1. [Test your code](testing.md)
1. [Write resilient code](resiliency.md)
1. [Remote Targets](remotetargets.md)
1. [Pass certain special types in arguments or return values](../exotic_types/index.md)
1. [Trace context](tracecontext.md)
1. [JoinableTaskFactory integration](joinableTaskFactory.md)
1. [Troubleshoot](troubleshooting.md)

See [full samples](https://github.com/AArnott/StreamJsonRpc.Sample) demonstrating two processes
on the same machine utilizing this library for RPC, using either named pipes or web sockets.

[Learn more about how you can customize the JSON-RPC protocol's wire format](extensibility.md) that StreamJsonRpc uses for better interoperability or better performance.

See also [Visual Studio specific concerns](vs.md).
