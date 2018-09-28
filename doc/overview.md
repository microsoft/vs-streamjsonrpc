# Overview

## Protocol overview

[JSON-RPC][spec] is a two-party, peer-to-peer based protocol by which one party can request a method's invocation
of the other party, and optionally receive a response from the server with the result of that invocation.

While we may often use the words 'client' and 'server' in discussions and documentation around JSON-RPC,
these are just convenient terms for referring to the party that is requesting a method invocation vs. the
party that is serving that request. At any given moment, *either party* may send an RPC request to the other party.

A common pattern is that one party tends to issue most of the RPC requests, while the other party may occasionally
transmit requests as a "call back" to the client for raising notifications. This is merely an artifact of architectural
expediency for many applications and not due to any design of the JSON-RPC protocol, or this library's particular
implementation of it.

For this reason, our documentation is organized around individual scenarios rather than on the client and server roles.

## StreamJsonRpc's role

StreamJsonRpc is a .NET library that implements the JSON-RPC protocol and exposes it via a .NET API to easily
send and receive RPC requests.

StreamJsonRpc works on any transport (e.g. Stream, WebSocket, Pipe).

## Security

The fundamental feature of the JSON-RPC protocol is the ability to request code execution of another party, including
passing data either direction that may influence code execution.
Neither the JSON-RPC protocol nor this library attempts to address the applicable security risks entailed.

Before establishing a JSON-RPC connection with a party that exists outside your own trust boundary, consider
the threats and how to mitigate them at your application level.

[spec]: https://www.jsonrpc.org/specification
