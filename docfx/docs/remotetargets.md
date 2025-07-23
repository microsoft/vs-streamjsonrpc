# Adding Remote RPC Targets

There are scenarios where users may need to add remote RPC targets to facilitate communication between two endpoints that have no direct RPC connection channel. Consider the following 3 endpoints:

* client
* server
* remote

There is a direct RPC connection between client and server, and server and remote. However, client and remote may need to send messages to each other as well. To do so, users can utilize the remote target feature:

``` csharp
var serverToClientRpc = new JsonRpc(clientStream, new ClientTarget());
var serverToRemoteRpc = new JsonRpc(remoteStream, new RemoteTarget());

// This allows the client endpoint to send messages to the remote endpoint.
serverToClientRpc.AddRemoteTarget(serverToRemoteRpc);

// This allows the remote endpoint to send messages to the client endpoint.
serverToRemoteRpc.AddRemoteTarget(serverToClientRpc);
```

### Intercepting Remote Target Calls

The client can only make a call to the remote endpoint if the server (what's in the middle) hasn't defined a method with the same signature. For example, consider the following:

``` csharp
public class ServerTarget
{
    public int GetInt()
    {
        return 5;
    }
}

public class RemoteTarget
{
    public int GetInt()
    {
        return 10;
    }
}

public static class Sample
{
    public static async Task InterceptionTest()
    {
        var clientRpc = new JsonRpc(clientServerStream);
        var serverClientRpc = new JsonRpc(serverClientStream, new ServerTarget());
        var serverRemoteRpc = new JsonRpc(serverRemoteStream);
        var remoteServerRpc = new JsonRpc(remoteServerStream, new RemoteTarget());

        serverClientRpc.AddRemoteTarget(serverRemoteRpc);

        clientRpc.StartListening();
        serverClientRpc.StartListening();
        remoteServerRpc.StartListening();

        var result = clientRpc.InvokeAsync("GetInt");
    }
}
```

The `result` captured in `InterceptionTest()` would be 5 instead of 10. When invoking methods on the remote target, StreamJsonRpc will first check to see if there are local methods matching the invocation signature. Only when it does not find a method will it forward the call to the remote target.
