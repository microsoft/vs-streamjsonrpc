namespace FSharpSample

open System
open System.IO.Pipes
open System.Threading.Tasks
open StreamJsonRpc

/// Defines the RPC contract shared by the client and server.
type IGreeter =
    /// Returns a greeting for the specified name.
    abstract GreetAsync: name: string -> Task<string>

/// Implements the RPC contract on the server.
type Greeter() =
    interface IGreeter with
        member _.GreetAsync(name) =
            Task.FromResult($"Hello, {name}!")

module Program =
    [<EntryPoint>]
    let main _ =
        try
            task {
                let pipeName = $"streamjsonrpc-fsharp-{Guid.NewGuid():N}"

                use serverPipe =
                    new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    )

                use clientPipe =
                    new NamedPipeClientStream(
                        ".",
                        pipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous
                    )

                let serverConnection = serverPipe.WaitForConnectionAsync()
                do! clientPipe.ConnectAsync()
                do! serverConnection

                use serverRpc = new JsonRpc(serverPipe)
                serverRpc.AddLocalRpcTarget<IGreeter>(Greeter(), JsonRpcTargetOptions())
                serverRpc.StartListening()

                use clientRpc = new JsonRpc(clientPipe)
                let server = clientRpc.Attach<IGreeter>()
                clientRpc.StartListening()

                let! greeting = server.GreetAsync("F#")
                printfn "%s" greeting
                return 0
            }
            |> fun operation -> operation.GetAwaiter().GetResult()
        with ex ->
            eprintfn "%O" ex
            1
