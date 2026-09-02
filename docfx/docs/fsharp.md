# F# sample

The following executable sample hosts an RPC server and client in the same process and connects them over a named pipe.
The `IGreeter` interface is the RPC contract shared by both sides.

[!code-fsharp[](../../samples/fs/Program.fs)]

Run the sample test from the repository root:

```console
dotnet test --project samples/fs/FSharpSample.fsproj
```
