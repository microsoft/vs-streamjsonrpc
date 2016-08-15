using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcServerInteropTests : TestBase
{
    private readonly Server server;
    private readonly Stream serverStream;
    private readonly JsonRpc serverRpc;

    private readonly Stream clientStream;
    private readonly DirectMessageHandler messageHandler;

    public JsonRpcServerInteropTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.messageHandler = new DirectMessageHandler(this.clientStream, this.serverStream, Encoding.UTF8);
        this.serverRpc = new JsonRpc(this.messageHandler, this.server);
        this.serverRpc.StartListening();
    }

    [Fact]
    public async Task ServerAcceptsObjectForMessageId()
    {
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoInt",
            @params = new[] { 5 },
            id = new { a = "b" },
        });

        Assert.Equal(5, (int)response.result);
        Assert.Equal("b", (string)response.id.a);
    }

    [Fact]
    public async Task ServerAcceptsNumberForMessageId()
    {
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoInt",
            @params = new[] { 5 },
            id = 1,
        });

        Assert.Equal(5, (int)response.result);
        Assert.Equal(1, (int)response.id);
    }

    [Fact]
    public async Task ServerAcceptsStringForMessageId()
    {
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoInt",
            @params = new[] { 5 },
            id = "abc",
        });

        Assert.Equal(5, (int)response.result);
        Assert.Equal("abc", (string)response.id);
    }

    [Fact]
    public async Task ServerAcceptsEmptyObjectForMessageId()
    {
        var response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoInt",
            @params = new[] { 5 },
            id = new { },
        });

        Assert.Equal(5, (int)response["result"]);
        Assert.Equal(0, response["id"].Count());
    }

    private Task<JObject> RequestAsync(object request)
    {
        this.Send(request);
        return this.ReceiveAsync();
    }

    private void Send(dynamic message)
    {
        Requires.NotNull(message, nameof(message));

        var json = JsonConvert.SerializeObject(message);
        this.messageHandler.OutboundMessages.Enqueue(json);
    }

    private async Task<JObject> ReceiveAsync()
    {
        string json = await this.messageHandler.IncomingMessages.DequeueAsync(this.TimeoutToken);
        return JObject.Parse(json);
    }

    private class Server
    {
        public int EchoInt(int value) => value;
    }
}
