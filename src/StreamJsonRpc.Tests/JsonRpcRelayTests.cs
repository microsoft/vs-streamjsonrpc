using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcRelayTests : TestBase
{
    private JsonRpc localRpc;
    private JsonRpc serverRpc;
    private JsonRpc relayServerRpc;

    public JsonRpcRelayTests(ITestOutputHelper logger)
       : base(logger)
    {
        var streams = FullDuplexStream.CreatePair();
        this.localRpc = JsonRpc.Attach(streams.Item2, new LocalOriginClient());
        this.serverRpc = new JsonRpc(streams.Item1, streams.Item1, new OriginServer());

        this.serverRpc.AddLocalRpcTarget(new OriginServer());
        this.serverRpc.StartListening();

        var relayStreams = Nerdbank.FullDuplexStream.CreateStreams();
        var relayServerStream = relayStreams.Item1;
        var relayClientStream = relayStreams.Item2;

        this.relayServerRpc = new JsonRpc(relayServerStream, relayServerStream, new RelayServer());
        this.relayServerRpc.StartListening();

        this.localRpc.Relay(relayClientStream, relayClientStream, new LocalRelayClient());
    }

    [Fact]
    public async Task CanInvokeOnRelayServer()
    {
        string result1 = await this.serverRpc.InvokeAsync<string>(nameof(RelayServer.RelayServerSayHello), "foo");
        Assert.Equal("Hello foo", result1);
    }

    [Fact]
    public async Task CanInvokeOnOriginServer()
    {
        string result1 = await this.relayServerRpc.InvokeAsync<string>(nameof(OriginServer.OriginServerSayGoodbye), "foo");
        Assert.Equal("Goodbye foo", result1);
    }

    [Fact]
    public async Task CanInvokeOnRelayClient()
    {
        string result1 = await this.relayServerRpc.InvokeAsync<string>(nameof(LocalRelayClient.LocalRelayClientSayHi), "foo");
        Assert.Equal("Hi foo from LocalRelayClient", result1);
    }

    [Fact]
    public async Task LocalOriginClientOverridesRelayServer()
    {
        string result1 = await this.serverRpc.InvokeAsync<string>("GetName");
        Assert.Equal(nameof(LocalOriginClient), result1);
    }

    [Fact]
    public async Task LocalRelayClientOverridesOriginServer()
    {
        string result1 = await this.relayServerRpc.InvokeAsync<string>("GetName");
        Assert.Equal(nameof(LocalRelayClient), result1);
    }

    public class RelayServer
    {
        public static string RelayServerSayHello(string name)
        {
            return "Hello " + name;
        }

        public string GetName()
        {
            return nameof(RelayServer);
        }
    }

    public class LocalOriginClient
    {
        public static string LocalOriginClientSayHi(string name)
        {
            return $"Hi {name} from {nameof(LocalOriginClient)}";
        }

        public string GetName()
        {
            return nameof(LocalOriginClient);
        }
    }

    public class LocalRelayClient
    {
        public static string LocalRelayClientSayHi(string name)
        {
            return $"Hi {name} from {nameof(LocalRelayClient)}";
        }

        public string GetName()
        {
            return nameof(LocalRelayClient);
        }
    }

    public class OriginServer
    {
        public static string OriginServerSayGoodbye(string name)
        {
            return "Goodbye " + name;
        }

        public string GetName()
        {
            return nameof(OriginServer);
        }
    }
}
