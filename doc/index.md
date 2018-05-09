# StreamJsonRpc Documentation

See [full samples](https://github.com/AArnott/StreamJsonRpc.Sample) demonstrating two processes
on the same machine utilizing this library for RPC, using either named pipes or web sockets.

See also [Visual Studio specific concerns](vs.md).

## Establishing Connection
There are two ways to establish connection and start invoking methods remotely:

1. Use the static `Attach` method in `JsonRpc` class:
```csharp
public void ConstructRpc(Stream stream)
{
    var target = new LanguageServerTarget();
    var rpc = JsonRpc.Attach(stream, target);
}
```
The `JsonRpc` object returned by `Attach` method would be used to invoke remote methods via JSON-RPC.

2. Construct a `JsonRpc` object directly:
```csharp
public void ConstructRpc(Stream clientStream, Stream serverStream)
{
    var clientRpc = new JsonRpc(clientStream, serverStream);
    var target = new Server();
    clientRpc.AddLocalRpcTarget(target);
    clientRpc.StartListening();
}
```

## Invoking a notification
To invoke a remote method named "foo" which takes one `string` parameter and does not return anything (i.e. send a notification remotely):
```csharp
public async Task NotifyRemote(Stream stream) 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(stream, target);
    await rpc.NotifyAsync("foo", "param1");
}
```
The parameter will be passed remotely as an array of one object.

To invoke a remote method named "bar" which takes one `string` parameter (but the parameter should be passed as an object instead of an array of one object):
```csharp
public async Task NotifyRemote(Stream stream) 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(stream, target);
    await rpc.NotifyWithParameterObjectAsync("bar", "param1");
}
```
## Invoking a request
To invoke a remote method named "foo" which takes two `string` parameters and returns an int:
```csharp
public async Task InvokeRemote(Stream stream) 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(stream, target);
    var myResult = await rpc.InvokeAsync<int>("foo", "param1", "param2");
}
```
The parameters will be passed remotely as an array of objects.

To invoke a remote method named "baz" which takes one `string` parameter (but the parameter should be passed as an object instead of an array of one object) and returns a string:
```csharp
public async Task InvokeRemote(Stream stream) 
{
    var target = new Server();
    var rpc = JsonRpc.Attach(stream, target);
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
    public async Task InvokeRemote(Stream stream) 
    {
        var target = new Server();
        var rpc = JsonRpc.Attach(stream, target);
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
```
With this attribute on the server method, the client can now invoke that method with a special name.
```csharp
public class Connection 
{
    public async Task InvokeRemote(Stream stream) 
    {
        var rpc = JsonRpc.Attach(stream);
        var myResult = await rpc.InvokeWithParameterObjectAsync<string>("test/InvokeTestMethod");
    }
}
```

If all your RPC method names follow a consistent transform from their C# method name equivalents,
you can use the `AddLocalTargetObject` method to transform the method names and avoid decorating
each one with an attribute. For example, given the same `Server` class above, but without the
`JsonRpcMethod` attribute, you can add a `test/` prefix to the method name with:

```csharp
var serverRpc = new JsonRpc(sendingStream, receivingStream);
serverRpc.AddLocalRpcTarget(new Server(), name => "test/" + name);
serverRpc.StartListening();
```

Some common method name transformations are available on the `CommonMethodNameTransforms` class.
For example:

```csharp
serverRpc.AddLocalRpcTarget(new Server(), CommonMethodNameTransforms.CamelCase);
```

## Close stream on fatal errors
In some cases, you may want to immediately close the streams if certain exceptions are thrown. In this case, overriding the `IsFatalException` method will give you the desired functionality. Through `IsFatalException` you can access and respond to exceptions as they are observed.
```csharp
public class Server : BaseClass
{
    public void ThrowsException() => throw new Exception("Throwing an exception");
}

public class JsonRpcClosesStreamOnException : JsonRpc
{
    public JsonRpcClosesStreamOnException(Stream clientStream, Stream serverStream, object target = null) : base(clientStream, serverStream, target)
    {
    }

    protected override bool IsFatalException(Exception ex)
    {
        return true;
    }
}

public class Connection
{
    public async Task InvokeRemote(Stream clientStream, Stream serverStream)
    {
        var target = new Server();
        var rpc = new JsonRpcClosesStreamOnException(clientStream, serverStream, target);
        rpc.StartListening();
        await rpc.InvokeAsync(nameof(Server.ThrowsException));
    }
}
```
