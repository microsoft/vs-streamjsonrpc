# StreamJsonRpc Documentation

## Establishing Connection
There are two ways to establish connection and start invoking methods remotely:

1. Use the static `Attach` method in `JsonRpc` class:
```csharp
public void ConstructRpc()
{
    var target = new LanguageServerTarget();
    var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
}
```
The `JsonRpc` object returned by `Attach` method would be used to invoke remote methods via JSON-RPC.

2. Construct a `JsonRpc` object directly:
```csharp
public void ConstructRpc()
{
    var streams = Nerdbank.FullDuplexStream.CreateStreams();
    var rpc = new JsonRpc(streams.Item1, streams.Item2);
    var target = new Server();
    rpc.AddLocalRpcTarget(target);
    rpc.StartListening();
}
```

## Invoking a notification
To invoke a remote method named "foo" which takes one `string` parameter and does not return anything (i.e. send a notification remotely):
```csharp
public async Task NotifyRemote() 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
    await rpc.NotifyAsync("foo", "param1");
}
```
The parameter will be passed remotely as an array of one object.

To invoke a remote method named "bar" which takes one `string` parameter (but the parameter should be passed as an object instead of an array of one object):
```csharp
public async Task NotifyRemote() 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
    await rpc.NotifyWithParameterObjectAsync("bar", "param1");
}
```
## Invoking a request
To invoke a remote method named "foo" which takes two `string` parameters and returns an int:
```csharp
public async Task NotifyRemote() 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
    var myResult = await rpc.InvokeAsync<int>("foo", "param1", "param2");
}
```
The parameters will be passed remotely as an array of objects.

To invoke a remote method named "baz" which takes one `string` parameter (but the parameter should be passed as an object instead of an array of one object) and returns a string:
```csharp
public async Task NotifyRemote() 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
    var myResult = await rpc.InvokeWithParameterObjectAsync<string>("baz", "param1");
}
```

## Receiving remote notifications and requests
To receive remote notifications and requests, construct your `JsonRpc` object with a target object.  Public methods on the target methods can be called remotely.  Any exceptions thrown in the method will be handled by StreamJsonRpc during transmission.
```csharp
public class Server
{
    public static string ServerMethod(string argument)
    {
        return argument + "!";
    }

    public static string TestParameter(JToken token)
    {
        return "object " + token.ToString();
    }
}

public class Connection 
{
    public async Task NotifyRemote() 
    {
        var target = new Server();
        var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
        var myResult = await rpc.InvokeWithParameterObjectAsync<string>("baz", "param1");
    }
}
```

## Invoking a method attribute
Sometimes you may want to invoke methods remotely following a standard protocol.  The names on the methods may not be legal method names for C#.  In this case, you can supply a `MethodAttribute` on your method to offer a replacement name:
```csharp
public class Server : BaseClass
{
    [JsonRpcMethod("test/InvokeTestMethod")]
    public string InvokeTestMethodAttribute() => "test method attribute";
}

public class Connection 
{
    public async Task NotifyRemote() 
    {
        var target = new Server();
        var rpc = JsonRpc.Attach(Console.OpenStandardOutput(), Console.OpenStandardInput(), target);
        var myResult = await rpc.InvokeWithParameterObjectAsync<string>("test/InvokeTestMethod");
    }
}
```