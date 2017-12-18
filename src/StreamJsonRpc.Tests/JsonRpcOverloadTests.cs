using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;
using static JsonRpcMethodAttributeTests;

public class JsonRpcOverloadTests : TestBase
{
    private readonly Server server;
    private readonly FullDuplexStream serverStream;
    private readonly FullDuplexStream clientStream;
    private readonly JsonRpc clientRpc;

    public JsonRpcOverloadTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.clientRpc = new JsonRpcOverload(this.clientStream, this.serverStream, this.server);
        this.clientRpc.StartListening();
    }

    [Fact]
    public async Task CanInvokeMethodOnServer()
    {
        string testLine = "TestLine1" + new string('a', 1024 * 1024);
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), testLine);
        Assert.Equal(testLine + "!", result1);
    }

    [Fact]
    public async Task CanInvokeMethodThatReturnsCancelledTask()
    {
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeAsync(nameof(Server.ServerMethodThatReturnsCancelledTask)));
        Assert.Equal(CancellationToken.None, ex.CancellationToken);
    }

    [Fact]
    public async Task CannotPassExceptionFromServer()
    {
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.NotNull(exception.StackTrace);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), "testing"));
    }

    [Fact]
    public async Task CanCallAsyncMethodThatThrows()
    {
        Exception exception = await Assert.ThrowsAnyAsync<TaskCanceledException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrows)));
        Assert.NotNull(exception.StackTrace);

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrows)));
    }

    [Fact]
    public async Task ThrowsIfCannotFindMethod_Overload()
    {
        await Assert.ThrowsAsync(typeof(RemoteMethodNotFoundException), () => this.clientRpc.InvokeAsync("missingMethod", 50));

        var result = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), "testing");
        Assert.Equal("testing!", result);
    }

    // Covers bug https://github.com/Microsoft/vs-streamjsonrpc/issues/55
    // Covers bug https://github.com/Microsoft/vs-streamjsonrpc/issues/56
    [Fact]
    public async Task InvokeWithCancellationAsync_CancelOnFirstWriteToStream()
    {
        // TODO: remove the next line when https://github.com/Microsoft/vs-threading/issues/185 is fixed
        this.server.DelayAsyncMethodWithCancellation = true;

        // Repeat 10 times because https://github.com/Microsoft/vs-streamjsonrpc/issues/56 is a timing issue and we may miss it on the first attempt.
        for (int iteration = 0; iteration < 10; iteration++)
        {
            using (var cts = new CancellationTokenSource())
            {
                this.clientStream.BeforeWrite = (stream, buffer, offset, count) =>
                {
                    // Cancel on the first write, when the header is being written but the content is not yet.
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }
                };

                await Assert.ThrowsAsync<TaskCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token)).WithTimeout(UnexpectedTimeout);
                this.clientStream.BeforeWrite = null;
            }

            // Verify that json rpc is still operational after cancellation.
            // If the cancellation breaks the json rpc, like in https://github.com/Microsoft/vs-streamjsonrpc/issues/55, it will close the stream
            // and cancel the request, resulting in unexpected OperationCancelledException thrown from the next InvokeAsync
            string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), "a");
            Assert.Equal("a!", result);
        }
    }

    [Fact]
    public async Task CancelMessageSentWhileAwaitingResponse()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();

            // Ultimately, the server throws because it was canceled.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask.WithTimeout(UnexpectedTimeout));
#if !NET452
            Assert.Equal(cts.Token, ex.CancellationToken);
#endif
        }

        var result = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), "testing");
        Assert.Equal("testing!", result);
    }

    [Fact]
    public async Task InvokeWithCancellationAsync_CanCallCancellableMethodWithNoArgs()
    {
        Assert.Equal(5, await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.AsyncMethodWithCancellationAndNoArgs)));

        using (var cts = new CancellationTokenSource())
        {
            Task<int> resultTask = this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.AsyncMethodWithCancellationAndNoArgs), cancellationToken: cts.Token);
            cts.Cancel();
            try
            {
                int result = await resultTask;
                Assert.Equal(5, result);
            }
            catch (OperationCanceledException)
            {
                // this is also an acceptable result.
            }
        }
    }

    [Fact]
    public async Task CancelMayStillReturnErrorFromServer()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodFaultsAfterCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();
            this.server.AllowServerMethodToReturn.Set();

            await Assert.ThrowsAsync<TaskCanceledException>(() => invokeTask);
        }
    }

    [Fact]
    public async Task AsyncMethodThrows()
    {
        await Assert.ThrowsAsync<TaskCanceledException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsAsync)));
    }

    public class Server
    {
        internal const string ThrowAfterCancellationMessage = "Throw after cancellation";

        public bool DelayAsyncMethodWithCancellation { get; set; }

        public AsyncAutoResetEvent ServerMethodReached { get; } = new AsyncAutoResetEvent();

        public AsyncAutoResetEvent AllowServerMethodToReturn { get; } = new AsyncAutoResetEvent();

        public static string ServerMethod(string argument)
        {
            return argument + "!";
        }

        public Task ServerMethodThatReturnsCancelledTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetCanceled();
            return tcs.Task;
        }

        public void MethodThatThrowsUnauthorizedAccessException()
        {
            throw new UnauthorizedAccessException();
        }

        public async Task AsyncMethodThatThrows()
        {
            await Task.Yield();
            throw new Exception();
        }

        public async Task<string> AsyncMethodWithCancellation(string arg, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();

            // TODO: remove when https://github.com/Microsoft/vs-threading/issues/185 is fixed
            if (this.DelayAsyncMethodWithCancellation)
            {
                await Task.Delay(UnexpectedTimeout).WithCancellation(cancellationToken);
            }

            await this.AllowServerMethodToReturn.WaitAsync(cancellationToken);
            return arg + "!";
        }

        public async Task<int> AsyncMethodWithCancellationAndNoArgs(CancellationToken cancellationToken)
        {
            await Task.Yield();
            return 5;
        }

        public async Task<string> AsyncMethodFaultsAfterCancellation(string arg, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();
            await this.AllowServerMethodToReturn.WaitAsync();
            if (!cancellationToken.IsCancellationRequested)
            {
                var cancellationSignal = new AsyncManualResetEvent();
                using (cancellationToken.Register(() => cancellationSignal.Set()))
                {
                    await cancellationSignal;
                }
            }

            throw new InvalidOperationException(ThrowAfterCancellationMessage);
        }

        public async Task MethodThatThrowsAsync()
        {
            await Task.Run(() => throw new Exception());
        }
    }

    internal class JsonRpcOverload : JsonRpc
    {
        public JsonRpcOverload(Stream sendingStream, Stream receivingStream, object target = null)
            : base(sendingStream, receivingStream, target)
        {
        }

        protected override bool IsFatalException(Exception ex) => true;
    }
}
