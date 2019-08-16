using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcRelayTests : TestBase
{
    private JsonRpc localRpc;
    private JsonRpc originRpc;
    private JsonRpc remoteRpc1;
    private JsonRpc remoteRpc2;

    public JsonRpcRelayTests(ITestOutputHelper logger)
       : base(logger)
    {
        var streams = FullDuplexStream.CreatePair();
        this.localRpc = JsonRpc.Attach(streams.Item2, new LocalOriginTarget());
        this.localRpc.AllowModificationWhileListening = true;
        this.originRpc = new JsonRpc(streams.Item1, streams.Item1, new OriginTarget());

        this.originRpc.AddLocalRpcTarget(new OriginTarget());
        this.originRpc.StartListening();

        var remoteStreams1 = Nerdbank.FullDuplexStream.CreateStreams();
        var remoteServerStream1 = remoteStreams1.Item1;
        var remoteClientStream1 = remoteStreams1.Item2;

        var remoteStreams2 = Nerdbank.FullDuplexStream.CreateStreams();
        var remoteServerStream2 = remoteStreams2.Item1;
        var remoteClientStream2 = remoteStreams2.Item2;

        var remoteTarget1 = JsonRpc.Attach(remoteClientStream1, remoteClientStream1, new LocalRelayTarget());
        remoteTarget1.AllowModificationWhileListening = true;

        var remoteTarget2 = JsonRpc.Attach(remoteClientStream2, remoteClientStream2, new LocalRelayTarget());
        remoteTarget2.AllowModificationWhileListening = true;

        this.remoteRpc1 = new JsonRpc(remoteServerStream1, remoteServerStream1, new RemoteTargetOne());
        this.remoteRpc1.StartListening();

        this.remoteRpc2 = new JsonRpc(remoteServerStream2, remoteServerStream2, new RemoteTargetTwo());
        this.remoteRpc2.StartListening();

        this.localRpc.AddRemoteRpcTarget(remoteTarget1);
        this.localRpc.AddRemoteRpcTarget(remoteTarget2);
        remoteTarget1.AddRemoteRpcTarget(this.localRpc);
        remoteTarget2.AddRemoteRpcTarget(this.localRpc);
    }

    [Fact]
    public async Task CanInvokeOnRelayServer()
    {
        int result1 = await this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.AddOne), 1);
        Assert.Equal(2, result1);
    }

    [Fact]
    public async Task CanInvokeOnOriginServer()
    {
        string result1 = await this.remoteRpc1.InvokeAsync<string>(nameof(OriginTarget.OriginServerSayGoodbye), "foo");
        Assert.Equal("Goodbye foo", result1);
    }

    [Fact]
    public async Task CanInvokeOnRelayClient()
    {
        string result1 = await this.remoteRpc1.InvokeAsync<string>(nameof(LocalRelayTarget.LocalRelayClientSayHi), "foo");
        Assert.Equal($"Hi foo from {nameof(LocalRelayTarget)}", result1);
    }

    [Fact]
    public async Task LocalOriginClientOverridesRelayServer()
    {
        string result1 = await this.originRpc.InvokeAsync<string>("GetName");
        Assert.Equal(nameof(LocalOriginTarget), result1);
    }

    [Fact]
    public async Task LocalRelayClientOverridesOriginServer()
    {
        string result1 = await this.remoteRpc1.InvokeAsync<string>("GetName");
        Assert.Equal(nameof(LocalRelayTarget), result1);
    }

    [Fact]
    public async Task CanInvokeAdditionalRemoteTarget()
    {
        int result = await this.originRpc.InvokeAsync<int>(nameof(RemoteTargetTwo.AddTwo), 2);
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task AdditionRemoteTargetsInvokedInOrder()
    {
        string result = await this.originRpc.InvokeAsync<string>(nameof(RemoteTargetTwo.GetRemoteName));
        Assert.Equal($"Remote {nameof(RemoteTargetOne)}", result);
    }

    [Fact]
    public async Task CanInvokeOnOriginServerFromAdditionalRemoteTarget()
    {
        string result1 = await this.remoteRpc2.InvokeAsync<string>(nameof(OriginTarget.OriginServerSayGoodbye), "foo");
        Assert.Equal("Goodbye foo", result1);
    }

    public class RemoteTargetOne
    {
        public static int AddOne(int value)
        {
            return value + 1;
        }

        public string GetName()
        {
            return nameof(RemoteTargetOne);
        }

        public string GetRemoteName()
        {
            return $"Remote {nameof(RemoteTargetOne)}";
        }
    }

    public class RemoteTargetTwo
    {
        public static int AddTwo(int value)
        {
            return value + 2;
        }

        public string GetName()
        {
            return nameof(RemoteTargetOne);
        }

        public string GetRemoteName()
        {
            return $"Remote {nameof(RemoteTargetTwo)}";
        }
    }

    public class LocalOriginTarget
    {
        public static string LocalOriginClientSayHi(string name)
        {
            return $"Hi {name} from {nameof(LocalOriginTarget)}";
        }

        public string GetName()
        {
            return nameof(LocalOriginTarget);
        }
    }

    public class LocalRelayTarget
    {
        public static string LocalRelayClientSayHi(string name)
        {
            return $"Hi {name} from {nameof(LocalRelayTarget)}";
        }

        public string GetName()
        {
            return nameof(LocalRelayTarget);
        }
    }

    public class OriginTarget
    {
        public static string OriginServerSayGoodbye(string name)
        {
            return "Goodbye " + name;
        }

        public string GetName()
        {
            return nameof(OriginTarget);
        }
    }
}
