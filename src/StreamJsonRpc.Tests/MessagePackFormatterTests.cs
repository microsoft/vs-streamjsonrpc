using System;
using System.Collections.Generic;
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
    public void JsonRpcRequest_ArgsArray()
    {
        var original = new JsonRpcRequest
        {
            Id = 5,
            Method = "test",
            ArgumentsArray = new object[] { 5, "hi", new CustomType { Age = 8 } },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.Id, actual.Id);
        Assert.Equal(original.Method, actual.Method);
        Assert.Equal(original.ArgumentsArray[0], actual.ArgumentsArray[0]);
        Assert.Equal(original.ArgumentsArray[1], actual.ArgumentsArray[1]);
        Assert.Equal(((CustomType)original.ArgumentsArray[2]).Age, ((CustomType)actual.ArgumentsArray[2]).Age);
    }

    [Fact]
    public void JsonRpcResult()
    {
        var original = new JsonRpcResult
        {
            Id = 5,
            Result = new CustomType { Age = 7 },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.Id, actual.Id);
        Assert.Equal(((CustomType)original.Result).Age, ((CustomType)actual.Result).Age);
    }

    [Fact]
    public void JsonRpcError()
    {
        var original = new JsonRpcError
        {
            Id = 5,
            Error = new JsonRpcError.ErrorDetail
            {
                Code = JsonRpcErrorCode.InvocationError,
                Message = "Oops",
                Data = new CustomType { Age = 15 },
            },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.Id, actual.Id);
        Assert.Equal(original.Error.Code, actual.Error.Code);
        Assert.Equal(original.Error.Message, actual.Error.Message);
        Assert.Equal(((CustomType)original.Error.Data).Age, ((CustomType)actual.Error.Data).Age);
    }

    [Fact]
    public async Task BasicJsonRpc()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var formatter = new MessagePackFormatter(compress: false);

        var clientHandler = new LengthHeaderMessageHandler(clientStream.UsePipe(), formatter);
        var serverHandler = new LengthHeaderMessageHandler(serverStream.UsePipe(), formatter);

        var clientRpc = new JsonRpc(clientHandler);
        var serverRpc = new JsonRpc(serverHandler, new Server());

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
