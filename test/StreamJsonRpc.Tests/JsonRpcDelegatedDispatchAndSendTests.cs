using System.Diagnostics;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcDelegatedDispatchAndSendTests : TestBase
{
    private readonly Server server;
    private readonly DelegatedJsonRpc clientRpc;
    private readonly DelegatedJsonRpc serverRpc;

    public JsonRpcDelegatedDispatchAndSendTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();
        var streams = Nerdbank.FullDuplexStream.CreateStreams();

        this.clientRpc = new DelegatedJsonRpc(new HeaderDelimitedMessageHandler(streams.Item1));
        this.serverRpc = new DelegatedJsonRpc(new HeaderDelimitedMessageHandler(streams.Item2), this.server);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Information);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Information);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.clientRpc.StartListening();
        this.serverRpc.StartListening();
    }

    [Fact]
    public async Task DispatchRequestIsPassedCorrectTypeOfRequest()
    {
        await this.clientRpc.InvokeAsync<string>(nameof(Server.TestMethodAsync));
        Assert.Equal("StreamJsonRpc.JsonMessageFormatter+JsonRpcRequest", this.serverRpc.LastRequestDispatched?.GetType().FullName);
    }

    [Fact]
    public async Task DelegatedDispatcherCanDispatchInReverseOrderBasedOnTopLevelProperty()
    {
        this.serverRpc.EnableBuffering = true;
        var totalCallCount = 10;
        var taskList = new List<Task<int>>();

        for (int i = 0; i < totalCallCount; i++)
        {
            taskList.Add(this.clientRpc.InvokeAsync<int>(nameof(Server.GetCallCountAsync)));
        }

        await this.serverRpc.FlushRequestQueueAsync(totalCallCount);

        for (int i = 0; i < totalCallCount; i++)
        {
            var result = await taskList[i];
            Assert.Equal(totalCallCount - i, result);
        }
    }

#pragma warning disable CA1801 // use all parameters
    public class Server
    {
        private int callCounter = 0;

        public Task TestMethodAsync()
        {
            return Task.CompletedTask;
        }

        public Task<int> GetCallCountAsync()
        {
            int currentCount = Interlocked.Increment(ref this.callCounter);
            return Task.FromResult(currentCount);
        }

        public Task MethodThatThrowsAsync()
        {
            throw new InvalidProgramException();
        }
    }

    public class DelegatedJsonRpc : JsonRpc
    {
        private const string MessageOrderPropertyName = "messageOrder";

        private AsyncQueue<(JsonRpcRequest, TaskCompletionSource<bool>, Task<JsonRpcMessage>)> requestSignalQueue = new AsyncQueue<(JsonRpcRequest, TaskCompletionSource<bool>, Task<JsonRpcMessage>)>();
        private int messageCounter = 0;

        public DelegatedJsonRpc(IJsonRpcMessageHandler handler)
            : base(handler)
        {
        }

        public DelegatedJsonRpc(IJsonRpcMessageHandler handler, object target)
            : base(handler, target)
        {
        }

        public bool EnableBuffering { get; set; }

        public JsonRpcRequest? LastRequestDispatched { get; private set; }

        public async Task FlushRequestQueueAsync(int expectedCount)
        {
            var requests = new SortedList<int, (TaskCompletionSource<bool>, Task<JsonRpcMessage>)>();

            for (int i = 0; i < expectedCount; i++)
            {
                var entry = await this.requestSignalQueue.DequeueAsync();
                Assert.True(entry.Item1.TryGetTopLevelProperty<int>(MessageOrderPropertyName, out int messageOrder));

                Assert.False(requests.ContainsKey(messageOrder));
                requests.Add(messageOrder, (entry.Item2, entry.Item3));
            }

            foreach (var entry in requests.Values.Reverse())
            {
                entry.Item1.SetResult(true);
                await entry.Item2;
            }
        }

        protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(JsonRpcRequest request, TargetMethod targetMethod, CancellationToken cancellationToken)
        {
            this.LastRequestDispatched = request;
            TaskCompletionSource<JsonRpcMessage>? completionTcs = null;

            if (this.EnableBuffering)
            {
                TaskCompletionSource<bool> signalTask = new TaskCompletionSource<bool>();
                completionTcs = new TaskCompletionSource<JsonRpcMessage>();
                this.requestSignalQueue.TryEnqueue((request, signalTask, completionTcs.Task));

                await signalTask.Task;
            }

            JsonRpcMessage result = await base.DispatchRequestAsync(request, targetMethod, cancellationToken);
            completionTcs?.SetResult(result);
            return result;
        }

        protected override ValueTask SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (message is JsonRpcRequest request)
            {
                Assert.True(request.TrySetTopLevelProperty<int>(MessageOrderPropertyName, this.messageCounter++));
            }

            return base.SendAsync(message, cancellationToken);
        }
    }

#pragma warning restore CA1801 // use all parameters
}
