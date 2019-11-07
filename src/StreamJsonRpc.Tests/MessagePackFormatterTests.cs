using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class MessagePackFormatterTests : TestBase
{
    private readonly MessagePackFormatter formatter = new MessagePackFormatter();

    public MessagePackFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void JsonRpcRequest_PositionalArgs()
    {
        var original = new JsonRpcRequest
        {
            RequestId = new RequestId(5),
            Method = "test",
            ArgumentsList = new object[] { 5, "hi", new CustomType { Age = 8 } },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(original.Method, actual.Method);

        Assert.True(actual.TryGetArgumentByNameOrIndex(null, 0, typeof(int), out object? actualArg0));
        Assert.Equal(original.ArgumentsList[0], actualArg0);

        Assert.True(actual.TryGetArgumentByNameOrIndex(null, 1, typeof(string), out object? actualArg1));
        Assert.Equal(original.ArgumentsList[1], actualArg1);

        Assert.True(actual.TryGetArgumentByNameOrIndex(null, 2, typeof(CustomType), out object? actualArg2));
        Assert.Equal(((CustomType?)original.ArgumentsList[2])!.Age, ((CustomType)actualArg2!).Age);
    }

    [Fact]
    public void JsonRpcRequest_NamedArgs()
    {
        var original = new JsonRpcRequest
        {
            RequestId = new RequestId(5),
            Method = "test",
            NamedArguments = new Dictionary<string, object?>
            {
                { "Number", 5 },
                { "Message", "hi" },
                { "Custom", new CustomType { Age = 8 } },
            },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(original.Method, actual.Method);

        Assert.True(actual.TryGetArgumentByNameOrIndex("Number", -1, typeof(int), out object? actualArg0));
        Assert.Equal(original.NamedArguments["Number"], actualArg0);

        Assert.True(actual.TryGetArgumentByNameOrIndex("Message", -1, typeof(string), out object? actualArg1));
        Assert.Equal(original.NamedArguments["Message"], actualArg1);

        Assert.True(actual.TryGetArgumentByNameOrIndex("Custom", -1, typeof(CustomType), out object? actualArg2));
        Assert.Equal(((CustomType?)original.NamedArguments["Custom"])!.Age, ((CustomType)actualArg2!).Age);
    }

    [Fact]
    public void JsonRpcResult()
    {
        var original = new JsonRpcResult
        {
            RequestId = new RequestId(5),
            Result = new CustomType { Age = 7 },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(((CustomType?)original.Result)!.Age, actual.GetResult<CustomType>().Age);
    }

    [Fact]
    public void JsonRpcError()
    {
        var original = new JsonRpcError
        {
            RequestId = new RequestId(5),
            Error = new JsonRpcError.ErrorDetail
            {
                Code = JsonRpcErrorCode.InvocationError,
                Message = "Oops",
                Data = new CustomType { Age = 15 },
            },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(original.Error.Code, actual.Error!.Code);
        Assert.Equal(original.Error.Message, actual.Error.Message);
        Assert.Equal(((CustomType)original.Error.Data).Age, actual.Error.GetData<CustomType>().Age);
    }

    [Fact]
    public async Task BasicJsonRpc()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var clientFormatter = new MessagePackFormatter(compress: false);
        var serverFormatter = new MessagePackFormatter(compress: false);

        var clientHandler = new LengthHeaderMessageHandler(clientStream.UsePipe(), clientFormatter);
        var serverHandler = new LengthHeaderMessageHandler(serverStream.UsePipe(), serverFormatter);

        var clientRpc = new JsonRpc(clientHandler);
        var serverRpc = new JsonRpc(serverHandler, new Server());

        serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        clientRpc.StartListening();
        serverRpc.StartListening();

        int result = await clientRpc.InvokeAsync<int>(nameof(Server.Add), 3, 5).WithCancellation(this.TimeoutToken);
        Assert.Equal(8, result);
    }

    private T Roundtrip<T>(T value)
        where T : JsonRpcMessage
    {
        var sequence = new Sequence<byte>();
        this.formatter.Serialize(sequence, value);
        var actual = (T)this.formatter.Deserialize(sequence);
        return actual;
    }

    [DataContract]
    public class CustomType
    {
        [DataMember]
        public int Age { get; set; }
    }

    private class Server
    {
        public int Add(int a, int b) => a + b;
    }
}
