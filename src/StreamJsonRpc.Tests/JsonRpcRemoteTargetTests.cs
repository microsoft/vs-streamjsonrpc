using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcRemoteTargetTests : InteropTestBase
{
    private readonly JsonRpc localRpc;
    private readonly RemoteTargetJsonRpc originRpc;
    private readonly JsonRpc remoteRpc1;
    private readonly JsonRpc remoteRpc2;
    private readonly RemoteTargetJsonRpc remoteTarget1;
    private readonly JsonRpc interopLocalRpc;

    public JsonRpcRemoteTargetTests(ITestOutputHelper logger)
       : base(logger, serverTest: true)
    {
        /* originRpc is the RPC connection from origin to local.
         * localRpc is the RPC connection from local to origin.
         * remoteTarget is the RPC connection from local to remote.
         * remoteRpc* is the RPC connection from remote to local. */

        var streams = FullDuplexStream.CreatePair();
        this.localRpc = new JsonRpc(streams.Item2);
        this.localRpc.AllowModificationWhileListening = true;
        this.localRpc.StartListening();

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

        this.localRpc.AddLocalRpcTarget(new LocalOriginTarget(this.remoteTarget1));
        this.localRpc.AddRemoteRpcTarget(this.remoteTarget1);
        this.localRpc.AddRemoteRpcTarget(remoteTarget2);
        this.remoteTarget1.AddRemoteRpcTarget(this.localRpc);
        remoteTarget2.AddRemoteRpcTarget(this.localRpc);

        this.interopLocalRpc = new JsonRpc(this.messageHandler, new LocalRelayTarget());
        this.interopLocalRpc.AddRemoteRpcTarget(this.remoteTarget1);
        this.interopLocalRpc.StartListening();
    }

    [Fact]
    public async Task CanInvokeOnRemoteTargetServer()
    {
        int result1 = await this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.AddOne), 1);
        Assert.Equal(2, result1);
    }

    [Fact]
    public async Task CanNotifyOnRemoteTargetServer()
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
    public async Task CanInvokeOnRemoteTargetClient()
    {
        string result1 = await this.remoteRpc1.InvokeAsync<string>(nameof(LocalRelayTarget.LocalRelayClientSayHi), "foo");
        Assert.Equal($"Hi foo from {nameof(LocalRelayTarget)}", result1);
    }

    [Fact]
    public async Task LocalOriginClientOverridesRemoteTargetserver()
    {
        string result1 = await this.originRpc.InvokeAsync<string>("GetName");
        Assert.Equal(nameof(LocalOriginTarget), result1);
    }

    [Fact]
    public async Task LocalRemoteTargetClientOverridesOriginServer()
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
        Task<bool> task = this.remoteRpc1.InvokeWithCancellationAsync<bool>(nameof(OriginTarget.CancellableOriginOperation), cancellationToken: tokenSource.Token);
        await OriginTarget.CancellableOriginOperationReached.WaitAsync(this.TimeoutToken);
        tokenSource.Cancel();
        bool result = await task;
        Assert.True(result);
    }

    [Fact]
    public async Task InvokeRemoteTargetWithExistingId()
    {
        var resultLocalTask = this.remoteTarget1.InvokeAsync<int>(new RequestId(1), nameof(RemoteTargetOne.AddTwo), 3, CancellationToken.None);
        var resultRemoteTask = this.originRpc.InvokeAsync<int>(new RequestId(1), nameof(RemoteTargetOne.AddOneLongRunningAsync), 1, CancellationToken.None);

        await Task.WhenAll(resultLocalTask, resultRemoteTask);

        Assert.Equal(5, resultLocalTask.Result);
        Assert.Equal(2, resultRemoteTask.Result);
    }

    [Fact]
    public async Task InvokeRemoteTargetThrowsException()
    {
        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.originRpc.InvokeAsync<bool>(nameof(RemoteTargetOne.GenerateException), 1));
        Assert.Equal("Invalid " + nameof(RemoteTargetOne.GenerateException), exception.Message);
    }

    [Fact]
    public async Task InvokeRemoteWithStringId()
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
    public async Task VerifyMethodsAreInvokedInOrderBeforeYielding()
    {
        // Set up for this test:
        // - origin issues a request intended for remote, but is intercepted by local (GetIntAfterSleepAsync). Local method blocks the thread by sleeping for 100ms to ensure that it doesn't complete before second request is processed.
        // - origin issues another request bound for remote, this one is not intercepted by local (GetInvokeCountAsync)
        // - both requests are kicked off without awaits.
        // - SynchronizationContext waits for both requests to be in the queue and then processes.
        // - Verify that the local call is ordered first, followed by its call to remote target (first await), then followed by the relayed remote call (because the GetInvokeCountAsync has a delay).
        Counter.CurrentCount = 0;

        this.localRpc.SynchronizationContext = new RpcOrderPreservingSynchronizationContext();
        ((RpcOrderPreservingSynchronizationContext)this.localRpc.SynchronizationContext).ExpectedTasksQueued = 2;

        var relaySleepCallTask = this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.GetIntAfterSleepAsync));
        var remoteCallTask = this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.GetInvokeCountAsync));

        await Task.WhenAll(relaySleepCallTask, remoteCallTask);

        Assert.Equal(1, LocalOriginTarget.InvokeCount);
        Assert.Equal(2, relaySleepCallTask.Result);
        Assert.Equal(3, remoteCallTask.Result);
    }

    [Fact(Skip = "Unstable. See https://github.com/microsoft/vs-streamjsonrpc/issues/336")]
    public async Task VerifyMethodOrderingIsNotGuaranteedAfterYielding()
    {
        // Set up for this test:
        // - origin issues a request intended for remote, but is intercepted by local (GetIntAfterSleepAsync). Local method delays 100ms and then makes the call to remote target.
        // - origin issues another request bound for remote, this one is not intercepted by local (GetInvokeCountAsync)
        // - both requests are kicked off without awaits.
        // - SynchronizationContext waits for both requests to be in the queue and then processes.
        // - Verify that the local call is ordered first, followed by the relayed remote call, followed by the local call to remote target.  The local call to the remote target happens last because Task.Delay would yield and process the continuation off the synchronization context.
        // The delay time in GetIntAfterDelayAsync is also longer than the delay in GetInvokeCountAsync.
        Counter.CurrentCount = 0;

        this.localRpc.SynchronizationContext = new RpcOrderPreservingSynchronizationContext();
        ((RpcOrderPreservingSynchronizationContext)this.localRpc.SynchronizationContext).ExpectedTasksQueued = 2;

        var relayDelayCallTask = this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.GetIntAfterDelayAsync));
        var remoteCallTask = this.originRpc.InvokeAsync<int>(nameof(RemoteTargetOne.GetInvokeCountAsync));

        await Task.WhenAll(relayDelayCallTask, remoteCallTask);

        Assert.Equal(1, LocalOriginTarget.InvokeCount);
        Assert.Equal(3, relayDelayCallTask.Result);
        Assert.Equal(2, remoteCallTask.Result);
    }

    public static class Counter
    {
        public static int CurrentCount;
    }

    public class RemoteTargetJsonRpc : JsonRpc
    {
        public RemoteTargetJsonRpc(Stream stream)
            : base(stream)
        {
        }

        public RemoteTargetJsonRpc(Stream sendingStream, Stream receivingStream, object? target = null)
            : base(sendingStream, receivingStream, target)
        {
        }

        public RemoteTargetJsonRpc(IJsonRpcMessageHandler messageHandler, object? target)
            : base(messageHandler, target)
        {
        }

        public RemoteTargetJsonRpc(IJsonRpcMessageHandler messageHandler)
            : base(messageHandler)
        {
        }

        public Task<T> InvokeAsync<T>(RequestId requestId, string targetName, object? argument, CancellationToken token)
        {
            var arguments = new object?[] { argument };
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

        public static bool GenerateException(int value)
        {
            throw new InvalidOperationException("Invalid " + nameof(GenerateException));
        }

        public Task<int> GetIntAfterSleepAsync()
        {
            var result = Interlocked.Increment(ref Counter.CurrentCount);
            return Task.FromResult(result);
        }

        public Task<int> GetIntAfterDelayAsync()
        {
            var result = Interlocked.Increment(ref Counter.CurrentCount);
            return Task.FromResult(result);
        }

        public async Task<int> GetInvokeCountAsync()
        {
            await Task.Delay(50);
            return Interlocked.Increment(ref Counter.CurrentCount);
        }

        public string GetName()
        {
            return nameof(RemoteTargetOne);
        }

        public string GetRemoteName()
        {
            return $"Remote {nameof(RemoteTargetOne)}";
        }

        public int EchoInt(int value) => value;
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
        private readonly JsonRpc remoteRpc;

        public LocalOriginTarget(JsonRpc remoteRpc)
        {
            this.remoteRpc = remoteRpc;
        }

        public static int InvokeCount
        {
            get;
            private set;
        }

        public static string LocalOriginClientSayHi(string name)
        {
            return $"Hi {name} from {nameof(LocalOriginTarget)}";
        }

        public async Task<int> GetIntAfterSleepAsync()
        {
            Thread.Sleep(100);
            InvokeCount = Interlocked.Increment(ref Counter.CurrentCount);

            return await this.remoteRpc.InvokeAsync<int>(nameof(RemoteTargetOne.GetIntAfterSleepAsync));
        }

        public async Task<int> GetIntAfterDelayAsync()
        {
            InvokeCount = Interlocked.Increment(ref Counter.CurrentCount);

            await Task.Delay(100);
            return await this.remoteRpc.InvokeAsync<int>(nameof(RemoteTargetOne.GetIntAfterDelayAsync));
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
        public static readonly AsyncAutoResetEvent CancellableOriginOperationReached = new AsyncAutoResetEvent();

        private static readonly TaskCompletionSource<int> NotificationTcs = new TaskCompletionSource<int>();

        public static Task<int> NotificationReceived => NotificationTcs.Task;

        public static void GetTwo()
        {
            NotificationTcs.SetResult(2);
        }

        public static string OriginServerSayGoodbye(string name)
        {
            return "Goodbye " + name;
        }

        public static async Task<bool> CancellableOriginOperation(CancellationToken token)
        {
            CancellableOriginOperationReached.Set();
            try
            {
                await Task.Delay(UnexpectedTimeout, token);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }

        public string GetName()
        {
            return nameof(OriginTarget);
        }
    }
}
