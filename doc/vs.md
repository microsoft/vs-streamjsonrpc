# Visual Studio specific concerns

## Versions to reference for Visual Studio extensions

When building a Visual Studio extension, you should not include StreamJsonRpc in your VSIX. Instead, you should compile your extension against the version of StreamJsonRpc that is included in the Visual Studio version that you are targeting as your minimum supported version and rely on Visual Studio's copy of the library to be present at runtime.

| VS version | StreamJsonRpc version |
| -- | -- |
| VS 2017.0	| 1.0.x
| VS 2017.3	| 1.1.x
| VS 2017.5	| 1.2.x
| VS 2017.6	| 1.3.x
| VS 2019.0	| 1.5.x
| VS 2019.1 | 1.5.x, 2.0.x
| VS 2019.3 | 1.5.x, 2.1.x
| VS 2019.4 | 1.5.x, 2.2.x
| VS 2019.5 | 1.5.x, 2.3.x

StreamJsonRpc versions are forwards and backwards compatible "over the wire". For example it is perfectly legitimate to use StreamJsonRpc 1.4 on the server-side even if the client only uses 1.0, or vice versa.

StreamJsonRpc is binary compatible within a major version. If you compile against 1.3 for targeting VS 2017.6, you'll successfully run against the StreamJsonRpc 1.5 version when installed in a later version of Visual Studio.
StreamJsonRpc 2.0 introduced breaking changes, so folks who compile against 1.x will continue to run on 1.x, while folks who want the additional functionality of 2.0 may recompile against that and work in VS 2019.1 and later.
