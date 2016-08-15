using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcClientInteropTests : TestBase
{
    private readonly Stream serverStream;

    private readonly Stream clientStream;
    private readonly JsonRpc clientRpc;
    private readonly DirectMessageHandler messageHandler;

    public JsonRpcClientInteropTests(ITestOutputHelper logger)
        : base(logger)
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.messageHandler = new DirectMessageHandler(this.clientStream, this.serverStream, Encoding.UTF8);
        this.clientRpc = new JsonRpc(this.messageHandler);
        this.clientRpc.StartListening();
    }

    [Fact(Skip = "Not yet implemented")]
    public async Task CancelMessageNotSentAfterResponseIsReceived()
    {
        await Task.Yield();
    }
}
