using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcWithFatalExceptionsTests : TestBase
{
    private readonly Server server;
    private readonly IJsonRpcMessageHandler clientMessageHandler;
    private readonly IJsonRpcMessageHandler serverMessageHandler;
    private readonly JsonRpc clientRpc;
    private readonly JsonRpcWithFatalExceptions serverRpc;

    public JsonRpcWithFatalExceptionsTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();
        var streams = Nerdbank.FullDuplexStream.CreateStreams();

        this.clientMessageHandler = new HeaderDelimitedMessageHandler(streams.Item1, streams.Item1);
        this.serverMessageHandler = new HeaderDelimitedMessageHandler(streams.Item2, streams.Item2);

        this.clientRpc = new JsonRpc(this.clientMessageHandler);
        this.serverRpc = new JsonRpcWithFatalExceptions(this.serverMessageHandler, this.server);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Information);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Information);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.clientRpc.StartListening();
        this.serverRpc.StartListening();
    }

    [Fact]
    public async Task CanInvokeMethodOnServer_WithVeryLargePayload()
    {
        string testLine = "TestLine1" + new string('a', 1024 * 1024);
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), testLine);
        Assert.Equal(testLine + "!", result1);
    }

    [Fact]
    public async Task StreamsStayOpenIfCancelledTaskReturned()
    {
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeAsync(nameof(Server.ServerMethodThatReturnsCancelledTask)));
        Assert.Equal(CancellationToken.None, ex.CancellationToken);
        Assert.NotNull(ex.StackTrace);
        Assert.Equal(0, this.serverRpc.IsFatalExceptionCount);

        // Assert that the JsonRpc and MessageHandler objects are not disposed
        Assert.False(((IDisposableObservable)this.clientRpc).IsDisposed);
        Assert.False(((IDisposableObservable)this.serverRpc).IsDisposed);
        Assert.False(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
    }

    [Fact]
    public async Task CloseStreamsOnSynchronousMethodException()
    {
        var exceptionMessage = "Exception from CloseStreamsOnSynchronousMethodException";
        ConnectionLostException exception = await Assert.ThrowsAnyAsync<ConnectionLostException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException), exceptionMessage));
        Assert.NotNull(exception.StackTrace);
        Assert.Equal(exceptionMessage, this.serverRpc.FaultException!.Message);
        Assert.Equal(1, this.serverRpc.IsFatalExceptionCount);

        Assert.True(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
        Assert.True(((IDisposableObservable)this.serverMessageHandler).IsDisposed);
        Assert.False(this.clientRpc.IsDisposed);
        Assert.False(this.serverRpc.IsDisposed);
    }

    [Fact]
    public async Task CloseStreamOnAsyncYieldAndThrowException()
    {
        var exceptionMessage = "Exception from CloseStreamOnAsyncYieldAndThrowException";
        Exception exception = await Assert.ThrowsAnyAsync<ConnectionLostException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrowsAfterYield), exceptionMessage));
        Assert.NotNull(exception.StackTrace);
        Assert.Equal(exceptionMessage, this.serverRpc.FaultException!.Message);
        Assert.Equal(1, this.serverRpc.IsFatalExceptionCount);

        Assert.True(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
        Assert.True(((IDisposableObservable)this.serverMessageHandler).IsDisposed);
        Assert.False(this.clientRpc.IsDisposed);
        Assert.False(this.serverRpc.IsDisposed);
    }

    [Fact]
    public async Task CloseStreamOnAsyncThrowExceptionAndYield()
    {
        var exceptionMessage = "Exception from CloseStreamOnAsyncThrowExceptionAndYield";
        Exception exception = await Assert.ThrowsAnyAsync<ConnectionLostException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrowsBeforeYield), exceptionMessage));
        Assert.NotNull(exception.StackTrace);
        Assert.Equal(exceptionMessage, this.serverRpc.FaultException!.Message);
        Assert.Equal(1, this.serverRpc.IsFatalExceptionCount);

        Assert.True(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
        Assert.True(((IDisposableObservable)this.serverMessageHandler).IsDisposed);
        Assert.False(this.clientRpc.IsDisposed);
        Assert.False(this.serverRpc.IsDisposed);
    }

    [Fact]
    public async Task CloseStreamOnAsyncTMethodException()
    {
        var exceptionMessage = "Exception from CloseStreamOnAsyncTMethodException";
        await Assert.ThrowsAnyAsync<ConnectionLostException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatReturnsStringAndThrows), exceptionMessage));
        Assert.Equal(exceptionMessage, this.serverRpc.FaultException!.Message);
        Assert.Equal(1, this.serverRpc.IsFatalExceptionCount);

        Assert.True(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
        Assert.True(((IDisposableObservable)this.serverMessageHandler).IsDisposed);
        Assert.False(this.clientRpc.IsDisposed);
        Assert.False(this.serverRpc.IsDisposed);
    }

    [Fact]
    public async Task StreamsStayOpenForNonServerException()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync("missingMethod", 50));

        // Assert MessageHandler object is not disposed
        Assert.False(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
    }

    [Fact]
    public async Task StreamsStayOpenOnOperationCanceled()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();

            // Ultimately, the server throws because it was canceled.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask.WithTimeout(UnexpectedTimeout));
            Assert.Equal(0, this.serverRpc.IsFatalExceptionCount);
        }

        // Assert that the JsonRpc and MessageHandler objects are not disposed
        Assert.False(((IDisposableObservable)this.clientRpc).IsDisposed);
        Assert.False(((IDisposableObservable)this.serverRpc).IsDisposed);
        Assert.False(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
    }

    [Fact]
    public async Task CancelExceptionPreferredOverConnectionLost()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodFaultsAfterCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();
            this.server.AllowServerMethodToReturn.Set();

            // When the remote hangs up while the local side has an outstanding request,
            // we expect ConnectionLostException to be thrown locally unless the request was already canceled anyway.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask);
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.Equal(Server.ThrowAfterCancellationMessage, this.serverRpc.FaultException!.Message);
            Assert.Equal(1, this.serverRpc.IsFatalExceptionCount);
        }

        Assert.True(((IDisposableObservable)this.clientMessageHandler).IsDisposed);
        Assert.True(((IDisposableObservable)this.serverMessageHandler).IsDisposed);
        Assert.False(this.clientRpc.IsDisposed);
        Assert.False(this.serverRpc.IsDisposed);
    }

    [Fact]
    public async Task AggregateExceptionIsNotRemovedFromAsyncMethod()
    {
        var remoteException = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeAsync(nameof(Server.AsyncMethodThrowsAggregateExceptionWithTwoInner)));

        // The async server method itself strips the second of the InnerExceptions, so we can't recover it here.
        // Since we only get one, we expect the inner exception (of the AggregateException)
        Assert.IsType<InvalidOperationException>(this.serverRpc.FaultException);
    }

    [Fact]
    public async Task AggregateExceptionIsNotRemovedFromTaskReturningSyncMethod()
    {
        var remoteException = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeAsync(nameof(Server.SyncMethodReturnsFaultedTaskWithAggregateExceptionWithTwoInner)));

        // The async server method itself strips the second of the InnerExceptions, so we can't recover it here.
        // Since we only get one, we expect the inner exception (of the AggregateException)
        Assert.IsType<InvalidOperationException>(this.serverRpc.FaultException);
    }

    [Fact]
    public async Task AggregateExceptionIsNotRemovedFromSyncMethod()
    {
        var remoteException = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeAsync(nameof(Server.SyncMethodThrowsAggregateException)));
        var filterException = (AggregateException)this.serverRpc.FaultException!;
        Assert.Equal(2, filterException.InnerExceptions.Count);
    }

    protected override void Dispose(bool disposing)
    {
        if (this.serverRpc.Completion.IsFaulted)
        {
            this.Logger.WriteLine("Server faulted with: " + this.serverRpc.Completion.Exception);
        }

        base.Dispose(disposing);
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

        public void MethodThatThrowsUnauthorizedAccessException(string message)
        {
            throw new UnauthorizedAccessException(message);
        }

        public async Task AsyncMethodThatThrowsAfterYield(string message)
        {
            await Task.Yield();
            throw new Exception(message);
        }

        public Task AsyncMethodThatThrowsBeforeYield(string message)
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetException(new Exception(message));
            return tcs.Task;
        }

        public async Task<string> AsyncMethodThatReturnsStringAndThrows(string message)
        {
            await Task.Yield();
            throw new Exception(message);

#pragma warning disable CS0162 // Unreachable code detected
            return "never will return";
#pragma warning restore CS0162
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

        public void SyncMethodThrowsAggregateException()
        {
            var failures = new List<Exception>();
            for (int i = 1; i <= 2; i++)
            {
                try
                {
                    FailingMethod(i);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException(failures);
            }
        }

        public async Task AsyncMethodThrowsAggregateExceptionWithTwoInner()
        {
            // This will throw an AggregateException with two inner exceptions.
            await Task.WhenAll(
                Task.Run(() => FailingMethod(1)),
                Task.Run(() => FailingMethod(2)));
        }

        public Task SyncMethodReturnsFaultedTaskWithAggregateExceptionWithTwoInner()
        {
            // This will return a Task with an AggregateException and two inner exceptions.
            return Task.WhenAll(
                Task.Run(() => FailingMethod(1)),
                Task.Run(() => FailingMethod(2)));
        }

        private static void FailingMethod(int number)
        {
            throw new InvalidOperationException($"Failure {number}");
        }
    }

    internal class JsonRpcWithFatalExceptions : JsonRpc
    {
        internal Exception? FaultException;

        internal int IsFatalExceptionCount;

        public JsonRpcWithFatalExceptions(IJsonRpcMessageHandler messageHandler, object? target = null)
            : base(messageHandler, target)
        {
            this.IsFatalExceptionCount = 0;
        }

        protected override bool IsFatalException(Exception ex)
        {
            this.FaultException = ex;
            this.IsFatalExceptionCount++;

            return true;
        }
    }
}
