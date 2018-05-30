# Visual Studio specific concerns

## Versions to reference for Visual Studio extensions

When building a Visual Studio extension, you should not include StreamJsonRpc in your VSIX. Instead, you should compile your extension against the version of StreamJsonRpc that is included in the Visual Studio version that you are targeting as your minimum supported version and rely on Visual Studio's copy of the library to be present at runtime.

| VS version | StreamJsonRpc version |
| -- | -- |
| VS 2017.0	| 1.0.x
| VS 2017.3	| 1.1.x
| VS 2017.5	| 1.2.x
| VS 2017.6	| 1.3.x
| TBD	| 1.4.x

StreamJsonRpc versions are forwards and backwards compatible "over the wire". For example it is perfectly legitimate to use StreamJsonRpc 1.4 on the server-side even if the client only uses 1.0, or vice versa.
