# F# sample

The following executable sample hosts an RPC server and calls it from a client over a named pipe.
The `IGreeter` interface is the RPC contract shared by both sides.

[!code-fsharp[](../../samples/fs/Program.fs)]

Run the sample from the repository root:

```console
dotnet run --project samples/fs/FSharpSample.fsproj
```
