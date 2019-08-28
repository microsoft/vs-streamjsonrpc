using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcRemoteTargetTests : TestBase
{
    private JsonRpc localRpc;
    private RemoteTargetJsonRpc originRpc;
    private JsonRpc remoteRpc1;
    private JsonRpc remoteRpc2;
    private RemoteTargetJsonRpc remoteTarget1;

    public JsonRpcRemoteTargetTests(ITestOutputHelper logger)
       : base(logger)
    {
        // originRpc is the RPC connection from origin to local.
        // localRpc is the RPC connection from local to origin.
        // remoteTarget is the RPC connection from local to remote.
        // remoteRpc* is the RPC connection from remote to local.

        var streams = FullDuplexStream.CreatePair();
        this.localRpc = JsonRpc.Attach(streams.Item2, new LocalOriginTarget());
        this.localRpc.AllowModificationWhileListening = true;
        this.originRpc = new RemoteTargetJsonRpc(streams.Item1, streams.Item1, new OriginTarget());

        this.originRpc.AddLocalRpcTarget(new OriginTarget());
        this.originRpc.StartListening();

        var remoteStreams1 = Nerdbank.FullDuplexStream.CreateStreams();
        var remoteServerStream1 = remoteStreams1.Item1;
        var remoteClientStream1 = remoteStreams1.Item2;

        var remoteStreams2 = Nerdbank.FullDuplexStream.CreateStreams();
        var remoteServerStream2 = remoteStreams2.Item1;
        var remoteClientStream2 = remoteStreams2.Item2;

        this.remoteTarget1 = new RemoteTargetJsonRpc(remoteClientStream1, remoteClientStream1, new LocalRelayTarget());
        this.remoteTarget1.AllowModificationWhileListening = true;
        this.remoteTarget1.StartListening();

        var remoteTarget2 = JsonRpc.Attach(remoteClientStream2, remoteClientStream2, new LocalRelayTarget());
        remoteTarget2.AllowModificationWhileListening = true;

        this.remoteRpc1 = new JsonRpc(remoteServerStream1, remoteServerStream1, new RemoteTargetOne());
        this.remoteRpc1.StartListening();

        this.remoteRpc2 = new JsonRpc(remoteServerStream2, remoteServerStream2, new RemoteTargetTwo());
        this.remoteRpc2.StartListening();

        this.localRpc.AddRemoteRpcTarget(this.remoteTarget1);
        this.localRpc.AddRemoteRpcTarget(remoteTarget2);
        this.remoteTarget1.AddRemoteRpcTarget(this.localRpc);
        remoteTarget2.AddRemoteRpcTarget(this.localRpc);
    }

    [Fact]
    public async Task CanInvokeOnRelayServer()
    {
        int result1 = await this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.AddOne), 1);
        Assert.Equal(2, result1);
    }

    [Fact]
    public async Task CanNotifyOnRelayServer()
    {
        await this.originRpc.NotifyAsync(nameof(RemoteTargetOne.GetOne));
        var result = await RemoteTargetOne.NotificationReceived;
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task CanInvokeOnOriginServer()
    {
        string result1 = await this.remoteRpc1.InvokeAsync<string>(nameof(OriginTarget.OriginServerSayGoodbye), "foo");
        Assert.Equal("Goodbye foo", result1);
    }

    [Fact]
    public async Task CanNotifyOnOriginServer()
    {
        await this.remoteRpc1.NotifyAsync(nameof(OriginTarget.GetTwo));
        var result = await OriginTarget.NotificationReceived;
        Assert.Equal(2, result);
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

    [Fact]
    public async Task CanCancelOnRemoteTarget()
    {
        var tokenSource = new CancellationTokenSource();
        var task = this.originRpc.InvokeWithCancellationAsync<bool>(nameof(RemoteTargetOne.CancellableRemoteOperation), cancellationToken: tokenSource.Token);
        tokenSource.Cancel();
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task CanCancelOnOriginTarget()
    {
        var tokenSource = new CancellationTokenSource();
        var task = this.remoteRpc1.InvokeWithCancellationAsync<bool>(nameof(OriginTarget.CancellableOriginOperation), cancellationToken: tokenSource.Token);
        tokenSource.Cancel();
        var result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task InvokeRemoteTargetWithExistingId()
    {
        var resultLocalTask = this.remoteTarget1.InvokeAsync<int>(1, nameof(RemoteTargetOne.AddTwo), 3, CancellationToken.None);
        var resultRemoteTask = this.originRpc.InvokeAsync<int>(1, nameof(RemoteTargetOne.AddOneLongRunningAsync), 1, CancellationToken.None);

        await Task.WhenAll(resultLocalTask, resultRemoteTask);

        Assert.Equal(5, resultLocalTask.Result);
        Assert.Equal(2, resultRemoteTask.Result);
    }

    public class RemoteTargetJsonRpc : JsonRpc
    {
        public RemoteTargetJsonRpc(Stream stream)
            : base(stream)
        {
        }

        public RemoteTargetJsonRpc(Stream sendingStream, Stream receivingStream, object target = null)
            : base(sendingStream, receivingStream, target)
        {
        }

        public RemoteTargetJsonRpc(IJsonRpcMessageHandler messageHandler, object target)
            : base(messageHandler, target)
        {
        }

        public RemoteTargetJsonRpc(IJsonRpcMessageHandler messageHandler)
            : base(messageHandler)
        {
        }

        public Task<T> InvokeAsync<T>(long requestId, string targetName, object argument, CancellationToken token)
        {
            var arguments = new object[] { argument };
            return this.InvokeCoreAsync<T>(requestId, targetName, arguments, token);
        }
    }

    public class RemoteTargetOne
    {
        private static TaskCompletionSource<int> notificationTcs = new TaskCompletionSource<int>();

        public static Task<int> NotificationReceived => notificationTcs.Task;

        public static void GetOne()
        {
            notificationTcs.SetResult(1);
        }

        public static int AddOne(int value)
        {
            return value + 1;
        }

        public static int AddTwo(int value)
        {
            return value + 2;
        }

        public static async Task<int> AddOneLongRunningAsync(int value)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            return value + 1;
        }

        public static async Task<bool> CancellableRemoteOperation(CancellationToken token)
        {
            var retryIndex = 0;
            while (retryIndex < 100)
            {
                await Task.Delay(100);
                if (token.IsCancellationRequested)
                {
                    return true;
                }
            }

            return false;
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
        private static TaskCompletionSource<int> notificationTcs = new TaskCompletionSource<int>();

        public static Task<int> NotificationReceived => notificationTcs.Task;

        public static void GetTwo()
        {
            notificationTcs.SetResult(2);
        }

        public static string OriginServerSayGoodbye(string name)
        {
            return "Goodbye " + name;
        }

        public static async Task<bool> CancellableOriginOperation(CancellationToken token)
        {
            var retryIndex = 0;
            while (retryIndex < 100)
            {
                await Task.Delay(100);
                if (token.IsCancellationRequested)
                {
                    return true;
                }
            }

            return false;
        }

        public string GetName()
        {
            return nameof(OriginTarget);
        }
    }
}
