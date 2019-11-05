// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public abstract class JsonRpcTests : TestBase
{
    protected readonly Server server;
    protected Nerdbank.FullDuplexStream serverStream;
    protected JsonRpc serverRpc;
    protected IJsonRpcMessageHandler serverMessageHandler;
    protected IJsonRpcMessageFormatter serverMessageFormatter;

    protected Nerdbank.FullDuplexStream clientStream;
    protected JsonRpc clientRpc;
    protected IJsonRpcMessageHandler clientMessageHandler;
    protected IJsonRpcMessageFormatter clientMessageFormatter;

    private const int CustomTaskResult = 100;

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
    public JsonRpcTests(ITestOutputHelper logger)
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        : base(logger)
    {
        TaskCompletionSource<JsonRpc> serverRpcTcs = new TaskCompletionSource<JsonRpc>();

        this.server = new Server();

        this.ReinitializeRpcWithoutListening();

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();
    }

    private interface IServer
    {
        [JsonRpcMethod("AnotherName")]
        string ARoseBy(string name);

        [JsonRpcMethod("IFaceNameForMethod")]
        int AddWithNameSubstitution(int a, int b);

        [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
        int InstanceMethodWithSingleObjectParameterAndCancellationToken(XAndYFields fields, CancellationToken token);
    }

    [Fact]
    public void Attach_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JsonRpc.Attach(stream: null!));
        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(sendingStream: null, receivingStream: null));
    }

    [Fact]
    public async Task Attach_NullSendingStream_CanOnlyReceiveNotifications()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var receivingStream = streams.Item1;
        var server = new Server();
        var rpc = JsonRpc.Attach(sendingStream: null, receivingStream: receivingStream, target: server);
        var disconnected = new AsyncManualResetEvent();
        rpc.Disconnected += (s, e) => disconnected.Set();

        var helperHandler = new HeaderDelimitedMessageHandler(sendingStream: streams.Item2, receivingStream: null);
        await helperHandler.WriteAsync(
            new JsonRpcRequest
            {
                Method = nameof(Server.NotificationMethod),
                ArgumentsList = new[] { "hello" },
            },
            this.TimeoutToken);

        Assert.Equal("hello", await server.NotificationReceived.WithCancellation(this.TimeoutToken));

        // Any form of outbound transmission should be rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.NotifyAsync("foo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.InvokeAsync("foo"));

        Assert.False(disconnected.IsSet);

        // Receiving a request should forcibly terminate the stream.
        await helperHandler.WriteAsync(
            new JsonRpcRequest
            {
                RequestId = new RequestId(1),
                Method = nameof(Server.MethodThatAccceptsAndReturnsNull),
                ArgumentsList = new object?[] { null },
            },
            this.TimeoutToken);

        // The connection should be closed because we can't send a response.
        await disconnected.WaitAsync().WithCancellation(this.TimeoutToken);

        // The method should not have been invoked.
        Assert.False(server.NullPassed);
    }

    [Fact]
    public async Task Attach_NullReceivingStream_CanOnlySendNotifications()
    {
        var sendingStream = new HalfDuplexStream();
        var rpc = JsonRpc.Attach(sendingStream: sendingStream, receivingStream: null);

        // Sending notifications is fine, as it's an outbound-only communication.
        await rpc.NotifyAsync("foo");

        // Verify that something was sent.
        await sendingStream.ReadAsync(new byte[1], 0, 1).WithCancellation(this.TimeoutToken);

        // Sending requests should not be allowed, since it requires waiting for a response.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.InvokeAsync("foo"));
    }

    [Fact]
    public void Ctor_Stream_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonRpc((Stream)null!));
    }

    /// <summary>
    /// Verifies tha the default message handler and formatter is as documented.
    /// </summary>
    [Fact]
    public async Task Ctor_Stream()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = new JsonRpc(this.serverStream);
        this.serverRpc.AddLocalRpcTarget(this.server);
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(this.clientStream, new JsonMessageFormatter()));
        this.clientRpc.StartListening();

        string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "hi");
        Assert.Equal("hi!", result);
    }

    [Fact]
    public async Task CanInvokeMethodOnServer_WithVeryLargePayload()
    {
        string testLine = "TestLine1" + new string('a', 1024 * 1024);
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), testLine);
        Assert.Equal(testLine + "!", result1);
    }

    [Fact]
    public async Task CanInvokeTaskMethodOnServer()
    {
        await this.clientRpc.InvokeAsync(nameof(Server.ServerMethodThatReturnsTask));
    }

    [Fact]
    public async Task NonGenericTaskServerMethod_ReturnsNullToClient()
    {
        object result = await this.clientRpc.InvokeAsync<object>(nameof(Server.ServerMethodThatReturnsTask));
        Assert.Null(result);
    }

    [Fact]
    public async Task CanInvokeMethodThatReturnsCustomTask()
    {
        int result = await this.clientRpc.InvokeAsync<int>(nameof(Server.ServerMethodThatReturnsCustomTask));
        Assert.StrictEqual(CustomTaskResult, result);
    }

    [Fact]
    public async Task CanInvokeMethodThatReturnsCancelledTask()
    {
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeAsync(nameof(Server.ServerMethodThatReturnsCancelledTask)));
        Assert.Equal(CancellationToken.None, ex.CancellationToken);
    }

    [Fact]
    public async Task InvokeWithCancellationAsync_ServerMethodSelfCancelsDoesNotReportWithOurToken()
    {
        var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ServerMethodThatReturnsCancelledTask), cancellationToken: cts.Token));
        Assert.Equal(CancellationToken.None, ex.CancellationToken);
    }

    [Fact]
    public async Task CanInvokeMethodThatReturnsTaskOfInternalClass()
    {
        // JsonRpc does not invoke non-public members in the default configuration. A public member cannot have Task<NonPublicType> result.
        // Though it can have result of just Task<object> type, which carries a NonPublicType instance.
        InternalClass result = await this.clientRpc.InvokeAsync<InternalClass>(nameof(Server.MethodThatReturnsTaskOfInternalClass));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CanCallMethodWithDefaultParameters()
    {
        var result = await this.clientRpc.InvokeAsync<int>(nameof(Server.MethodWithDefaultParameter), 10);
        Assert.Equal(20, result);

        result = await this.clientRpc.InvokeAsync<int>(nameof(Server.MethodWithDefaultParameter), 10, 20);
        Assert.Equal(30, result);
    }

    [Fact]
    public async Task CanPassNull_NullElementInArgsArray()
    {
        var result = await this.clientRpc.InvokeAsync<object>(nameof(Server.MethodThatAccceptsAndReturnsNull), new object?[] { null });
        Assert.Null(result);
        Assert.True(this.server.NullPassed);
    }

    [Fact]
    public async Task NullAsArgumentLiteral()
    {
        // This first one succeeds because null args is interpreted as 1 null argument.
        var result = await this.clientRpc.InvokeAsync<object>(nameof(Server.MethodThatAccceptsAndReturnsNull), null);
        Assert.Null(result);
        Assert.True(this.server.NullPassed);

        // This one fails because null literal is interpreted as a method that takes a parameter with a null argument.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<object>(nameof(Server.MethodThatAcceptsNothingAndReturnsNull), null));
        Assert.Null(result);
    }

    [Fact]
    public async Task CanSendNotification()
    {
        await this.clientRpc.NotifyAsync(nameof(Server.NotificationMethod), "foo").WithCancellation(this.TimeoutToken);
        Assert.Equal("foo", await this.server.NotificationReceived.WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task CanCallAsyncMethod()
    {
        string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "test");
        Assert.Equal("test!", result);
    }

    [Fact]
    public async Task CanCallAsyncMethodThatThrows()
    {
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrows)));
        Assert.NotNull(exception.ErrorData);
    }

    [Fact]
    public async Task CanCallOverloadedMethod()
    {
        int result = await this.clientRpc.InvokeAsync<int>(nameof(Server.OverloadedMethod), new Foo { Bar = "bar-bar", Bazz = -100 });
        Assert.Equal(1, result);

        result = await this.clientRpc.InvokeAsync<int>(nameof(Server.OverloadedMethod), 40);
        Assert.Equal(40, result);
    }

    [Fact]
    public async Task ThrowsIfCannotFindMethod()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync("missingMethod", 50));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.OverloadedMethod), new { X = 100 }));
    }

    [Fact]
    public async Task ThrowsIfTargetNotSet()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.serverRpc.InvokeAsync(nameof(Server.OverloadedMethod)));
    }

    [SkippableFact]
    [Trait("TestCategory", "FailsInCloudTest")] // Test showing unstability on Azure Pipelines, but always succeeds locally.
    public async Task InvokeWithProgressParameter_NoMemoryLeakConfirm()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");
        WeakReference weakRef = await this.InvokeWithProgressParameter_NoMemoryLeakConfirm_Helper();
        GC.Collect();
        Assert.False(weakRef.IsAlive);
    }

    [SkippableFact]
    public async Task NotifyWithProgressParameter_NoMemoryLeakConfirm()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");
        WeakReference weakRef = await this.NotifyAsyncWithProgressParameter_NoMemoryLeakConfirm_Helper();
        GC.Collect();
        Assert.False(weakRef.IsAlive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectedEventIsFired(bool disposeRpc)
    {
        var disconnectedEventFired = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();

        // Subscribe to disconnected event
        object? disconnectedEventSender = null;
        this.serverRpc.Disconnected += (object sender, JsonRpcDisconnectedEventArgs e) =>
        {
            disconnectedEventSender = sender;
            disconnectedEventFired.SetResult(e);
        };

        // Close server or client stream.
        if (disposeRpc)
        {
            this.serverRpc.Dispose();
        }
        else
        {
            this.serverStream.Dispose();
        }

        JsonRpcDisconnectedEventArgs args = await disconnectedEventFired.Task.WithCancellation(this.TimeoutToken);
        Assert.Same(this.serverRpc, disconnectedEventSender);
        Assert.NotNull(args);
        Assert.NotNull(args.Description);

        // Confirm that an event handler added after disconnection also gets raised.
        disconnectedEventFired = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();
        this.serverRpc.Disconnected += (object sender, JsonRpcDisconnectedEventArgs e) =>
        {
            disconnectedEventSender = sender;
            disconnectedEventFired.SetResult(e);
        };

        args = await disconnectedEventFired.Task;
        Assert.Same(this.serverRpc, disconnectedEventSender);
        Assert.NotNull(args);
        Assert.NotNull(args.Description);
    }

    [Fact]
    public async Task CanCallMethodOnBaseClass()
    {
        string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.BaseMethod));
        Assert.Equal("base", result);

        result = await this.clientRpc.InvokeAsync<string>(nameof(Server.VirtualBaseMethod));
        Assert.Equal("child", result);

        result = await this.clientRpc.InvokeAsync<string>(nameof(Server.RedeclaredBaseMethod));
        Assert.Equal("child", result);
    }

    [Fact]
    public async Task CannotCallMethodsOnSystemObject()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(object.ToString)));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(object.GetHashCode)));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(object.GetType)));
    }

    [Fact]
    public async Task NonPublicMethods_NotInvokableByDefault()
    {
        Assert.False(new JsonRpcTargetOptions().AllowNonPublicInvocation);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.InternalMethod)));
    }

    [Theory]
    [PairwiseData]
    public async Task NonPublicMethods_InvokableOnlyUnderOption(bool allowNonPublicInvocation, bool attributedMethod)
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = new JsonRpc(this.serverStream, this.serverStream);
        this.clientRpc = new JsonRpc(this.clientStream, this.clientStream);

        this.serverRpc.AddLocalRpcTarget(this.server, new JsonRpcTargetOptions { AllowNonPublicInvocation = allowNonPublicInvocation });

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        string methodName = attributedMethod ? nameof(Server.InternalMethodWithAttribute) : nameof(Server.InternalMethod);
        Task invocationAttempt = this.clientRpc.InvokeAsync(methodName);
        if (allowNonPublicInvocation)
        {
            await invocationAttempt;
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
        }
        else
        {
            await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => invocationAttempt);
        }
    }

    [Fact]
    public async Task CannotCallMethodWithOutParameter()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodWithOutParameter), 20));
    }

    [Fact]
    public async Task CannotCallMethodWithRefParameter()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodWithRefParameter), 20));
    }

    [Fact]
    public async Task CanCallMethodOmittingAsyncSuffix()
    {
        int result = await this.clientRpc.InvokeAsync<int>("MethodThatEndsIn");
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task NullableParameters()
    {
        int? result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodAcceptsNullableArgs), null, 3);
        Assert.Equal(1, result);
        result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodAcceptsNullableArgs), 3, null);
        Assert.Equal(1, result);
        result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodAcceptsNullableArgs), 3, 5);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task NullableReturnType()
    {
        int? result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodReturnsNullableInt), 0);
        Assert.Null(result);
        result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodReturnsNullableInt), 5);
        Assert.Equal(5, result);
    }

    [Fact]
    public async Task CanCallMethodWithoutOmittingAsyncSuffix()
    {
        int result = await this.clientRpc.InvokeAsync<int>("MethodThatEndsInAsync");
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CanCallMethodWithAsyncSuffixInPresenceOfOneMissingSuffix()
    {
        int result = await this.clientRpc.InvokeAsync<int>(nameof(Server.MethodThatMayEndInAsync));
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task CanCallMethodOmittingAsyncSuffixInPresenceOfOneWithSuffix()
    {
        int result = await this.clientRpc.InvokeAsync<int>(nameof(Server.MethodThatMayEndIn));
        Assert.Equal(5, result);
    }

    [Fact]
    public void SynchronizationContext_DefaultIsNull()
    {
        Assert.Null(this.serverRpc.SynchronizationContext);
    }

    [Fact]
    public void SynchronizationContext_SetterThrowsOnFixedConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.SynchronizationContext = new SynchronizationContext());
    }

    [Fact]
    public void SynchronizationContext_CanChangeWhileListening()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        SynchronizationContext syncContext = new SynchronizationContext();
        this.serverRpc.SynchronizationContext = syncContext;
        Assert.Same(syncContext, this.serverRpc.SynchronizationContext);
        this.serverRpc.SynchronizationContext = null;
        Assert.Null(this.serverRpc.SynchronizationContext);
    }

    [Fact]
    public async Task SynchronizationContext_InvocationOrderPreserved()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        var syncContext = new BlockingPostSynchronizationContext();
        this.serverRpc.SynchronizationContext = syncContext;
        var invoke1 = this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "arg1");
        var invoke2 = this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "arg1");

        // Assert that the second Post call on the server will not happen while the first Post hasn't returned.
        // This is the way we verify that processing incoming requests never becomes concurrent before the
        // invocation is sent to the SynchronizationContext.
        Task unblockingTask = await Task.WhenAny(invoke1, invoke2, syncContext.PostInvoked.WaitAsync()).WithCancellation(UnexpectedTimeoutToken);
        await unblockingTask; // rethrow any exception that may have occurred while we were waiting.

        await Task.Delay(ExpectedTimeout);
        Assert.Equal(1, syncContext.PostCalls);

        // Allow both calls to proceed.
        syncContext.AllowPostToReturn.Set();

        // Wait for them both to complete.
        await Task.WhenAll(invoke1, invoke2);

        // Just a sanity check that a second Post call bumps the number to validate our earlier assertions.
        Assert.Equal(2, syncContext.PostCalls);
    }

    [Fact]
    public async Task InvokeAsync_ServerMethodsAreInvokedOnSynchronizationContext()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        var syncContext = new ServerSynchronizationContext();
        this.serverRpc.SynchronizationContext = syncContext;
        const string serverMethodName = "SyncContextMethod";
        this.serverRpc.AddLocalRpcMethod(serverMethodName, new Func<bool>(() => syncContext.RunningInContext));
        bool inContext = await this.clientRpc.InvokeAsync<bool>(serverMethodName);
        Assert.True(inContext);
    }

    [Fact]
    public async Task NotifyAsync_ServerMethodsAreInvokedOnSynchronizationContext()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        var syncContext = new ServerSynchronizationContext();
        this.serverRpc.SynchronizationContext = syncContext;
        const string serverMethodName = "SyncContextMethod";
        var notifyResult = new TaskCompletionSource<bool>();
        this.serverRpc.AddLocalRpcMethod(serverMethodName, new Action(() => notifyResult.SetResult(syncContext.RunningInContext)));
        await this.clientRpc.NotifyAsync(serverMethodName).WithCancellation(this.TimeoutToken);
        bool inContext = await notifyResult.Task.WithCancellation(this.TimeoutToken);
        Assert.True(inContext);
    }

    [Fact]
    public async Task InvokeAsync_CanCallCancellableMethodWithoutCancellationToken()
    {
        this.server.AllowServerMethodToReturn.Set();
        string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodWithCancellation), "a").WithCancellation(this.TimeoutToken);
        Assert.Equal("a!", result);
    }

    [Fact]
    public async Task InvokeWithCancellationAsync_CanCallUncancellableMethod()
    {
        using (var cts = new CancellationTokenSource())
        {
            Task<string> resultTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethod), new[] { "a" }, cts.Token);
            cts.Cancel();
            string result = await resultTask;
            Assert.Equal("a!", result);
        }
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

                var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token)).WithTimeout(UnexpectedTimeout);
                Assert.Equal(cts.Token, ex.CancellationToken);
                this.clientStream.BeforeWrite = null;
            }

            // Verify that json rpc is still operational after cancellation.
            // If the cancellation breaks the json rpc, like in https://github.com/Microsoft/vs-streamjsonrpc/issues/55, it will close the stream
            // and cancel the request, resulting in unexpected OperationCancelledException thrown from the next InvokeAsync
            string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "a");
            Assert.Equal("a!", result);
        }
    }

    [Fact]
    public async Task Invoke_ThrowsCancellationExceptionOverDisposedException()
    {
        this.clientRpc.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => this.clientRpc.InvokeAsync("anything"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => this.clientRpc.InvokeWithCancellationAsync("anything", Array.Empty<object>(), CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync("anything", Array.Empty<object>(), new CancellationToken(true)));
    }

    [Fact]
    public async Task Invoke_ThrowsConnectionLostExceptionOverDisposedException()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            this.clientRpc.Dispose();
            this.server.AllowServerMethodToReturn.Set();

            // Connection was closed before error was sent from the server
            await Assert.ThrowsAnyAsync<ConnectionLostException>(() => invokeTask);
        }
    }

    [Fact]
    public async Task InvokeAsync_CanCallCancellableMethodWithNoArgs()
    {
        Assert.Equal(5, await this.clientRpc.InvokeAsync<int>(nameof(Server.AsyncMethodWithCancellationAndNoArgs)));
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
    public async Task InvokeAsync_PassArgsAsNonArrayList()
    {
        var args = new List<object> { 1, 2 };
        int result = await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.MethodWithDefaultParameter), args, this.TimeoutToken);
        Assert.Equal(3, result);
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
            Assert.Equal(cts.Token, ex.CancellationToken);
        }
    }

    [Fact]
    public async Task CancelMayStillReturnResultFromServer()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodIgnoresCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();
            this.server.AllowServerMethodToReturn.Set();
            string result = await invokeTask;
            Assert.Equal("a!", result);
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
            try
            {
                await invokeTask;
                Assert.False(true, "Expected exception not thrown.");
            }
            catch (RemoteInvocationException ex)
            {
                Assert.Equal(Server.ThrowAfterCancellationMessage, ex.Message);
            }
        }
    }

    [Fact]
    public async Task InvokeWithPrecanceledToken()
    {
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(this.server.AsyncMethodIgnoresCancellation), new[] { "a" }, cts.Token));
        }
    }

    [Fact]
    public async Task InvokeThenCancelToken()
    {
        using (var cts = new CancellationTokenSource())
        {
            this.server.AllowServerMethodToReturn.Set();
            await this.clientRpc.InvokeWithCancellationAsync(nameof(this.server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token);
            cts.Cancel();
        }
    }

    [Fact]
    [Trait("Category", "SkipWhenLiveUnitTesting")] // flaky test
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_UncancellableMethodWithoutCancellationToken()
    {
        await this.CheckGCPressureAsync(
            async delegate
            {
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethod), new object[] { "a" }));
            });
    }

    [Fact]
    [Trait("Category", "SkipWhenLiveUnitTesting")] // flaky test
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_UncancellableMethodWithCancellationToken()
    {
        var cts = new CancellationTokenSource();
        await this.CheckGCPressureAsync(
            async delegate
            {
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethod), new object[] { "a" }, cts.Token));
            });
    }

    [Fact]
    [Trait("Category", "SkipWhenLiveUnitTesting")] // flaky test
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_CancellableMethodWithoutCancellationToken()
    {
        await this.CheckGCPressureAsync(
            async delegate
            {
                this.server.AllowServerMethodToReturn.Set();
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethodWithCancellation), new object[] { "a" }, CancellationToken.None));
            });
    }

    [Fact]
    [Trait("Category", "SkipWhenLiveUnitTesting")] // flaky test
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_CancellableMethodWithCancellationToken()
    {
        var cts = new CancellationTokenSource();
        await this.CheckGCPressureAsync(
            async delegate
            {
                this.server.AllowServerMethodToReturn.Set();
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethodWithCancellation), new object[] { "a" }, cts.Token));
            });
    }

    [Fact]
    [Trait("Category", "SkipWhenLiveUnitTesting")] // slow, and flaky test
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_CancellableMethodWithCancellationToken_Canceled()
    {
        await this.CheckGCPressureAsync(
            async delegate
            {
                var cts = new CancellationTokenSource();
                var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethodWithCancellation), new object[] { "a" }, cts.Token);
                cts.Cancel();
                this.server.AllowServerMethodToReturn.Set();
                await invokeTask.NoThrowAwaitable(); // may or may not throw due to cancellation (and its inherent race condition)
            });
    }

    [Fact]
    public async Task ServerReturnsCompletedTask()
    {
        await this.clientRpc.InvokeAsync(nameof(Server.ReturnPlainTask));
    }

    [Fact]
    public async Task ServerReturnsCompletedValueTask()
    {
        await this.clientRpc.InvokeAsync(nameof(Server.ReturnPlainValueTaskNoYield));
    }

    [Fact]
    public async Task ServerReturnsYieldingValueTask()
    {
        // Make sure that JsonRpc recognizes a returned ValueTask as a yielding async method rather than just returning immediately.
        Task invokeTask = this.clientRpc.InvokeAsync(nameof(Server.ReturnPlainValueTaskWithYield));
        await Task.Delay(ExpectedTimeout);
        Assert.False(invokeTask.IsCompleted);
        this.server.AllowServerMethodToReturn.Set();
        await invokeTask.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task ServerReturnsNoYieldValueTaskOfT()
    {
        int sum = await this.clientRpc.InvokeAsync<int>(nameof(Server.AddValueTaskNoYield), 1, 2);
        Assert.Equal(3, sum);
    }

    [Fact]
    public async Task ServerReturnsYieldingValueTaskOfT()
    {
        int sum = await this.clientRpc.InvokeAsync<int>(nameof(Server.AddValueTaskWithYield), 1, 2);
        Assert.Equal(3, sum);
    }

    [Fact]
    public async Task CanInvokeServerMethodWithNoParameterPassedAsObject()
    {
        string result1 = await this.clientRpc.InvokeWithParameterObjectAsync<string>(nameof(Server.TestParameter));
        Assert.Equal("object or array", result1);
    }

    [Fact]
    public async Task InvokeWithParameterObject_Fields()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithDefaultParameter), new XAndYFields { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithParameterObject_DefaultParameters()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithDefaultParameter), new { x = 2 }, this.TimeoutToken);
        Assert.Equal(12, sum);
    }

    [SkippableFact]
    public async Task InvokeWithParameterObject_ProgressParameter()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");

        int report = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(n => report = n);

        int result = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressParameter), new { p = progress }, this.TimeoutToken);

        await progress.WaitAsync();

        Assert.Equal(1, report);
        Assert.Equal(1, result);
    }

    [SkippableFact]
    public async Task InvokeWithParameterObject_ProgressParameterMultipleRequests()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");

        int report1 = 0;
        ProgressWithCompletion<int> progress1 = new ProgressWithCompletion<int>(n => report1 = n);

        int report2 = 0;
        ProgressWithCompletion<int> progress2 = new ProgressWithCompletion<int>(n => report2 = n * 2);

        int report3 = 0;
        ProgressWithCompletion<int> progress3 = new ProgressWithCompletion<int>(n => report3 = n * 3);

        await this.InvokeMethodWithProgressParameter(progress1);
        await this.InvokeMethodWithProgressParameter(progress2);
        await this.InvokeMethodWithProgressParameter(progress3);

        await progress1.WaitAsync();
        await progress2.WaitAsync();
        await progress3.WaitAsync();

        Assert.Equal(1, report1);
        Assert.Equal(2, report2);
        Assert.Equal(3, report3);
    }

    [SkippableFact]
    public async Task InvokeWithParameterObject_InvalidParamMethod()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");

        int report = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(n => report = n);

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithInvalidProgressParameter), new { p = progress }, this.TimeoutToken));
    }

    [SkippableFact]
    public async Task InvokeWithParameterObject_ProgressParameterAndFields()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");

        int report = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(n => report += n);

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressAndMoreParameters), new { p = progress, x = 2, y = 5 }, this.TimeoutToken);

        await progress.WaitAsync();

        Assert.Equal(7, report);
        Assert.Equal(7, sum);
    }

    [SkippableFact]
    public async Task InvokeWithParameterObject_ProgressAndDefaultParameters()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");

        int report = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(n => report += n);

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressAndMoreParameters), new { p = progress, x = 2 }, this.TimeoutToken);

        await progress.WaitAsync();

        Assert.Equal(12, report);
        Assert.Equal(12, sum);
    }

    [SkippableFact]
    public async Task InvokeWithSingleObjectParameter_SendingExpectedObject()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "Single object deserialization is not supported for MessagePack");

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithSingleObjectParameter", new XAndYFields { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_ServerMethodExpectsObjectButDoesNotSetDeserializationProperty()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(async () => await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithSingleObjectParameterWithoutDeserializationProperty), new XAndYFields { x = 2, y = 5 }, this.TimeoutToken));
    }

    [SkippableFact]
    public async Task InvokeWithSingleObjectParameter_ServerMethodExpectsObjectButSendingDifferentType()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "Single object deserialization is not supported for MessagePack");

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithSingleObjectParameterVAndW", new XAndYFields { x = 2, y = 5 }, this.TimeoutToken);

        Assert.Equal(0, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_ServerMethodSetDeserializationPropertyButExpectMoreThanOneParameter()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(async () => await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithObjectAndExtraParameters", new XAndYFields { x = 2, y = 5 }, this.TimeoutToken));
    }

    [SkippableFact]
    public async Task InvokeWithSingleObjectParameter_SendingExpectedObjectAndCancellationToken()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "Single object deserialization is not supported for MessagePack");

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithSingleObjectParameterAndCancellationToken), new XAndYFields { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [SkippableFact]
    public async Task InvokeWithSingleObjectParameter_SendingExpectedObjectAndCancellationToken_InterfaceMethodAttributed()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "Single object deserialization is not supported for MessagePack");

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(IServer.InstanceMethodWithSingleObjectParameterAndCancellationToken), new XAndYFields { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [SkippableFact]
    public async Task InvokeWithSingleObjectParameter_SendingWithProgressProperty()
    {
        Skip.If(this.clientMessageFormatter is MessagePackFormatter, "IProgress<T> serialization is not supported for MessagePack");

        int report = 0;
        var progress = new ProgressWithCompletion<int>(n => report += n);

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithSingleObjectParameterWithProgress", new XAndYFieldsWithProgress { x = 2, y = 5, p = progress }, this.TimeoutToken);

        await progress.WaitAsync();

        Assert.Equal(7, report);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task CanInvokeServerMethodWithNoParameterPassedAsArray()
    {
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.TestParameter));
        Assert.Equal("object or array", result1);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionThrownIfServerHasMultipleMethodsMatched()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.TestInvalidMethod)));
    }

    [Fact]
    public void AddLocalRpcTarget_ExceptionThrownWhenRpcHasStartedListening()
    {
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.AddLocalRpcTarget(new AdditionalServerTargetOne()));
    }

    [Fact]
    public void AddLocalRpcTarget_ExceptionThrownWhenTargetIsNull()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1, streams.Item2);
        Assert.Throws<ArgumentNullException>(() => rpc.AddLocalRpcTarget(null!));
    }

    [Fact]
    public async Task AddLocalRpcTarget_AdditionalTargetMethodFound()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1, streams.Item2);
        rpc.AddLocalRpcTarget(new Server());
        rpc.AddLocalRpcTarget(new AdditionalServerTargetOne());
        rpc.AddLocalRpcTarget(new AdditionalServerTargetTwo());
        rpc.StartListening();

        var serverMethodResult = await rpc.InvokeAsync<string>(nameof(Server.ServerMethod), "test");
        Assert.Equal("test!", serverMethodResult);

        var plusOneResultInt = await rpc.InvokeAsync<int>(nameof(AdditionalServerTargetOne.PlusOne), 1);
        Assert.Equal(2, plusOneResultInt);

        var plusOneResultString = await rpc.InvokeAsync<string>(nameof(AdditionalServerTargetTwo.PlusOne), "one");
        Assert.Equal("one plus one!", plusOneResultString);

        var plusTwoResult = await rpc.InvokeAsync<int>(nameof(AdditionalServerTargetTwo.PlusTwo), 1);
        Assert.Equal(3, plusTwoResult);
    }

    [Fact]
    public async Task AddLocalRpcTarget_NoTargetContainsRequestedMethod()
    {
        var streams = FullDuplexStream.CreatePair();
        var localRpc = JsonRpc.Attach(streams.Item2);
        var serverRpc = new JsonRpc(streams.Item1, streams.Item1);
        serverRpc.AddLocalRpcTarget(new Server());
        serverRpc.AddLocalRpcTarget(new AdditionalServerTargetOne());
        serverRpc.AddLocalRpcTarget(new AdditionalServerTargetTwo());
        serverRpc.StartListening();

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => localRpc.InvokeAsync("PlusThree", 1));
    }

    [Fact]
    public async Task AddLocalRpcTarget_WithNamespace()
    {
        var streams = FullDuplexStream.CreatePair();
        var localRpc = JsonRpc.Attach(streams.Item2);
        var serverRpc = new JsonRpc(streams.Item1, streams.Item1);
        serverRpc.AddLocalRpcTarget(new Server());
        serverRpc.AddLocalRpcTarget(new AdditionalServerTargetOne(), new JsonRpcTargetOptions { MethodNameTransform = n => "one." + n });
        serverRpc.AddLocalRpcTarget(new AdditionalServerTargetTwo(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Prepend("two.") });
        serverRpc.StartListening();

        Assert.Equal("hi!", await localRpc.InvokeAsync<string>("ServerMethod", "hi"));
        Assert.Equal(6, await localRpc.InvokeAsync<int>("one.PlusOne", 5));
        Assert.Equal(7, await localRpc.InvokeAsync<int>("two.PlusTwo", 5));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => localRpc.InvokeAsync<int>("PlusTwo", 5));
    }

    [Fact]
    public async Task AddLocalRpcTarget_CamelCaseTransform()
    {
        // Verify that camel case doesn't work in the default case.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("serverMethod", "hi"));

        // Now set up a server with a camel case transform and verify that it works (and that the original casing doesn't).
        var streams = FullDuplexStream.CreatePair();
        var rpc = new JsonRpc(streams.Item1, streams.Item2);
        rpc.AddLocalRpcTarget(new Server(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase });
        rpc.StartListening();

        Assert.Equal("hi!", await rpc.InvokeAsync<string>("serverMethod", "hi"));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<string>("ServerMethod", "hi"));
    }

    /// <summary>
    /// Verify that the method name transform runs with the attribute-determined method name as an input.
    /// </summary>
    [Fact]
    public async Task AddLocalRpcTarget_MethodNameTransformAndRpcMethodAttribute()
    {
        // Now set up a server with a camel case transform and verify that it works (and that the original casing doesn't).
        var streams = FullDuplexStream.CreatePair();
        var rpc = new JsonRpc(streams.Item1, streams.Item2);
        rpc.AddLocalRpcTarget(new Server(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase });
        rpc.StartListening();

        Assert.Equal(3, await rpc.InvokeAsync<int>("classNameForMethod", 1, 2));

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<int>("ClassNameForMethod", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<int>("IFaceNameForMethod", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<int>("AddWithNameSubstitution", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<int>("iFaceNameForMethod", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<int>("ifaceNameForMethod", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync<int>("addWithNameSubstitution", 1, 2));
    }

    [Fact]
    public async Task AddLocalRpcMethod_ActionWith0Args()
    {
        this.ReinitializeRpcWithoutListening();

        bool invoked = false;
        this.serverRpc.AddLocalRpcMethod("biz.bar", new Action(() => invoked = true));
        this.StartListening();

        await this.clientRpc.InvokeAsync("biz.bar").WithCancellation(this.TimeoutToken);
        Assert.True(invoked);
    }

    [Fact]
    public async Task AddLocalRpcMethod_ActionWith1Args()
    {
        this.ReinitializeRpcWithoutListening();

        int expectedArg = 3;
        int actualArg = 0;
        this.serverRpc.AddLocalRpcMethod("biz.bar", new Action<int>(arg => actualArg = arg));
        this.StartListening();

        await this.clientRpc.InvokeAsync("biz.bar", expectedArg);
        Assert.Equal(expectedArg, actualArg);
    }

    [Fact]
    public async Task AddLocalRpcMethod_ActionWithMultipleOverloads()
    {
        this.ReinitializeRpcWithoutListening();

        const int expectedArg1 = 3;
        int actualArg1 = 0;
        const string expectedArg2 = "hi";
        string? actualArg2 = null;

        void Callback2(int n, string s)
        {
            actualArg1 = n;
            actualArg2 = s;
        }

        this.serverRpc.AddLocalRpcMethod("biz.bar", new Action<int>(arg => actualArg1 = arg));
        this.serverRpc.AddLocalRpcMethod("biz.bar", new Action<int, string>(Callback2));
        this.StartListening();

        await this.clientRpc.InvokeAsync("biz.bar", expectedArg1, expectedArg2);
        Assert.Equal(expectedArg1, actualArg1);
        Assert.Equal(expectedArg2, actualArg2);

        actualArg1 = 0;
        actualArg2 = null;
        await this.clientRpc.InvokeAsync("biz.bar", expectedArg1);
        Assert.Equal(expectedArg1, actualArg1);
        Assert.Null(actualArg2);
    }

    [Fact]
    public async Task AddLocalRpcMethod_FuncWith0Args()
    {
        this.ReinitializeRpcWithoutListening();

        bool invoked = false;
        this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<bool>(() => invoked = true));
        this.StartListening();

        Assert.True(await this.clientRpc.InvokeAsync<bool>("biz.bar"));
        Assert.True(invoked);
    }

    [Fact]
    public async Task AddLocalRpcMethod_AsyncFuncWith0Args()
    {
        this.ReinitializeRpcWithoutListening();

        bool invoked = false;
        async Task<bool> Callback()
        {
            await Task.Yield();
            invoked = true;
            return true;
        }

        this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<Task<bool>>(Callback));
        this.StartListening();

        Assert.True(await this.clientRpc.InvokeAsync<bool>("biz.bar"));
        Assert.True(invoked);
    }

    [Fact]
    public async Task AddLocalRpcMethod_FuncWith1Args()
    {
        this.ReinitializeRpcWithoutListening();

        int expectedArg = 3;
        int actualArg = 0;
        this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<int, int>(arg => actualArg = arg));
        this.StartListening();

        Assert.Equal(expectedArg, await this.clientRpc.InvokeAsync<int>("biz.bar", expectedArg));
        Assert.Equal(expectedArg, actualArg);
    }

    [Fact]
    public async Task AddLocalRpcMethod_FuncWithMultipleOverloads()
    {
        this.ReinitializeRpcWithoutListening();

        const int expectedArg1 = 3;
        int actualArg1 = 0;
        const string expectedArg2 = "hi";
        string? actualArg2 = null;
        const double expectedResult = 0.2;

        double Callback2(int n, string s)
        {
            actualArg1 = n;
            actualArg2 = s;
            return expectedResult;
        }

        this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<int, int>(arg => actualArg1 = arg));
        this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<int, string, double>(Callback2));
        this.StartListening();

        Assert.Equal(expectedResult, await this.clientRpc.InvokeAsync<double>("biz.bar", expectedArg1, expectedArg2));
        Assert.Equal(expectedArg1, actualArg1);
        Assert.Equal(expectedArg2, actualArg2);

        actualArg1 = 0;
        actualArg2 = null;
        Assert.Equal(expectedArg1, await this.clientRpc.InvokeAsync<int>("biz.bar", expectedArg1));
        Assert.Equal(expectedArg1, actualArg1);
        Assert.Null(actualArg2);
    }

    [Fact]
    public void AddLocalRpcMethod_FuncsThatDifferByReturnTypeOnly()
    {
        this.ReinitializeRpcWithoutListening();

        this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<int>(() => 1));
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<string>(() => "a")));
    }

    [Fact]
    public void AddLocalRpcMethod_ExceptionThrownWhenRpcHasStartedListening()
    {
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.AddLocalRpcMethod("biz.bar", new Func<int>(() => 1)));
    }

    [Fact]
    public void AddLocalRpcMethod_String_Delegate_ThrowsOnInvalidInputs()
    {
        this.ReinitializeRpcWithoutListening();

        Assert.Throws<ArgumentNullException>(() => this.serverRpc.AddLocalRpcMethod("biz.bar", null!));
        Assert.Throws<ArgumentException>(() => this.serverRpc.AddLocalRpcMethod(string.Empty, new Func<int>(() => 1)));
    }

    [Fact]
    public void AddLocalRpcMethod_String_MethodInfo_Object_ThrowsOnInvalidInputs()
    {
        this.ReinitializeRpcWithoutListening();

        MethodInfo methodInfo = typeof(Server).GetTypeInfo().DeclaredMethods.First();
        Assert.Throws<ArgumentNullException>(() => this.serverRpc.AddLocalRpcMethod("biz.bar", null!, this.server));
        Assert.Throws<ArgumentException>(() => this.serverRpc.AddLocalRpcMethod(string.Empty, methodInfo, this.server));
    }

    [Fact]
    public async Task AddLocalRpcMethod_String_MethodInfo_Object_NullTargetForStaticMethod()
    {
        this.ReinitializeRpcWithoutListening();

        MethodInfo methodInfo = typeof(Server).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(Server.ServerMethod));
        Assumes.True(methodInfo.IsStatic); // we picked this method because it's static.
        this.serverRpc.AddLocalRpcMethod("biz.bar", methodInfo, null);

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        string result = await this.clientRpc.InvokeAsync<string>("biz.bar", "foo");
        Assert.Equal("foo!", result);
    }

    [Fact]
    public async Task AddLocalRpcMethod_MethodInfo_Object_Attribute()
    {
        this.ReinitializeRpcWithoutListening();

        MethodInfo methodInfo = typeof(Server).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(Server.ServerMethod));
        Assumes.True(methodInfo.IsStatic); // we picked this method because it's static.
        this.serverRpc.AddLocalRpcMethod(methodInfo, null, new JsonRpcMethodAttribute("biz.bar"));

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        string result = await this.clientRpc.InvokeAsync<string>("biz.bar", "foo");
        Assert.Equal("foo!", result);
    }

    [Fact]
    public void AddLocalRpcMethod_String_MethodInfo_Object_NonNullTargetForStaticMethod()
    {
        this.ReinitializeRpcWithoutListening();

        MethodInfo methodInfo = typeof(Server).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(Server.ServerMethod));
        Assumes.True(methodInfo.IsStatic); // we picked this method because it's static.
        Assert.Throws<ArgumentException>(() => this.serverRpc.AddLocalRpcMethod("biz.bar", methodInfo, this.server));
    }

    [Fact]
    public void AddLocalRpcMethod_String_MethodInfo_Object_NullTargetForInstanceMethod()
    {
        this.ReinitializeRpcWithoutListening();

        MethodInfo methodInfo = typeof(Server).GetTypeInfo().DeclaredMethods.Single(m => m.Name == nameof(Server.ServerMethodInstance));
        Assumes.True(!methodInfo.IsStatic); // we picked this method because it's static.
        Assert.Throws<ArgumentException>(() => this.serverRpc.AddLocalRpcMethod("biz.bar", methodInfo, null));
    }

    [Fact]
    public async Task ServerMethodIsCanceledWhenConnectionDrops()
    {
        this.ReinitializeRpcWithoutListening();
        this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;
        this.clientRpc.StartListening();
        this.serverRpc.StartListening();

        Task rpcTask = this.clientRpc.InvokeAsync(nameof(Server.AsyncMethodWithCancellation), "arg");
        Assert.False(rpcTask.IsCompleted);
        await this.server.ServerMethodReached.WaitAsync();
        this.clientStream.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.server.ServerMethodCompleted.Task).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task ServerMethodIsNotCanceledWhenConnectionDrops()
    {
        Assert.False(this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed);
        Task rpcTask = this.clientRpc.InvokeAsync(nameof(Server.AsyncMethodWithCancellation), "arg");
        Assert.False(rpcTask.IsCompleted);
        await this.server.ServerMethodReached.WaitAsync();
        this.clientStream.Dispose();
        await Task.Delay(ExpectedTimeout);
        this.server.AllowServerMethodToReturn.Set();
        await this.server.ServerMethodCompleted.Task;
    }

    [Fact]
    public void CannotSetAutoCancelWhileListening()
    {
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true);
        Assert.False(this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed);
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;
        Assert.True(this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed);
    }

    [Fact]
    public void AllowModificationWhileListening_DefaultsToFalse()
    {
        Assert.False(this.serverRpc.AllowModificationWhileListening);
    }

    [Fact]
    public void StartListening_ThrowsWhenAlreadyListening_WhileAllowModifications()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.StartListening());
    }

    /// <summary>
    /// Verifies (with a great deal of help by interactively debugging and freezing a thread) that <see cref="JsonRpc.StartListening"/>
    /// shouldn't have a race condition with itself and a locally invoked RPC method calling <see cref="JsonRpc.InvokeCoreAsync{TResult}(RequestId, string, System.Collections.Generic.IReadOnlyList{object}, CancellationToken, bool)"/>.
    /// </summary>
    [Fact]
    public async Task StartListening_ShouldNotAllowIncomingMessageToRaceWithInvokeAsync()
    {
        this.ReinitializeRpcWithoutListening();

        var result = new TaskCompletionSource<object?>();
        this.clientRpc.AddLocalRpcMethod("nothing", new Action(() => { }));
        this.serverRpc.AddLocalRpcMethod(
            "race",
            new Func<Task>(async delegate
            {
                try
                {
                    await this.serverRpc.InvokeAsync("nothing");
                    result.SetResult(null);
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            }));

        this.clientRpc.StartListening();
        var clientInvokeTask = this.clientRpc.InvokeAsync("race");

        // For an effective test, the timing must be precise here.
        // Within the StartListening method, one must freeze the executing thread after it kicks off the listening task,
        // but BEFORE it assigns that Task to a field.
        // That thread must remain frozen until our "race" method above has completed its InvokeAsync call.
        // As of the time of this writing, there is in fact a race condition that will case the InvokeAsync call
        // to throw an exception claiming that StartListening has not yet been called.
        this.serverRpc.StartListening();

        await Task.WhenAll(clientInvokeTask, result.Task).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task AddLocalRpcMethod_AllowedAfterListeningIfOptIn()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        bool invoked = false;
        this.serverRpc.AddLocalRpcMethod("myNewMethod", new Action(() => invoked = true));
        await this.clientRpc.InvokeAsync("myNewMethod");
        Assert.True(invoked);
        this.serverRpc.AllowModificationWhileListening = false;
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.AddLocalRpcMethod("anotherMethodAbc", new Action(() => { })));
    }

    [Fact]
    public async Task AddLocalRpcTarget_AllowedAfterListeningIfOptIn()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new AdditionalServerTargetOne());
        int result = await this.clientRpc.InvokeAsync<int>(nameof(AdditionalServerTargetOne.PlusOne), 3);
        Assert.Equal(4, result);
        this.serverRpc.AllowModificationWhileListening = false;
        Assert.Throws<InvalidOperationException>(() => this.serverRpc.AddLocalRpcTarget(new AdditionalServerTargetTwo()));
    }

    [Fact]
    public void Completion_BeforeListeningAndAfterDisposal()
    {
        var rpc = new JsonRpc(Stream.Null, Stream.Null);
        Task completion = rpc.Completion;
        rpc.Dispose();
        Assert.True(completion.IsCompleted);
    }

    [Fact]
    public async Task Completion_CompletesOnRemoteStreamClose()
    {
        Task completion = this.serverRpc.Completion;
        this.clientRpc.Dispose();
        await completion.WithTimeout(UnexpectedTimeout);
        Assert.Same(completion, this.serverRpc.Completion);
    }

    [Fact]
    public async Task Completion_CompletesOnLocalDisposal()
    {
        Task completion = this.serverRpc.Completion;
        this.serverRpc.Dispose();
        await completion.WithTimeout(UnexpectedTimeout);
        Assert.Same(completion, this.serverRpc.Completion);
    }

    [Fact]
    public async Task MultipleSyncMethodsExecuteConcurrentlyOnServer()
    {
        var invocation1 = this.clientRpc.InvokeAsync(nameof(Server.SyncMethodWaitsToReturn));
        await this.server.ServerMethodReached.WaitAsync(UnexpectedTimeoutToken);
        var invocation2 = this.clientRpc.InvokeAsync(nameof(Server.SyncMethodWaitsToReturn));
        await this.server.ServerMethodReached.WaitAsync(UnexpectedTimeoutToken);
        this.server.AllowServerMethodToReturn.Set();
        this.server.AllowServerMethodToReturn.Set();
        await Task.WhenAll(invocation1, invocation2);
    }

    [Fact]
    public async Task ServerRespondsWithMethodRenamedByInterfaceAttribute()
    {
        Assert.Equal("ANDREW", await this.clientRpc.InvokeAsync<string>("AnotherName", "andrew"));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(IServer.ARoseBy), "andrew"));
    }

    [Fact]
    public async Task ClassDefinedNameOverridesInterfaceDefinedName()
    {
        Assert.Equal(3, await this.clientRpc.InvokeAsync<int>("ClassNameForMethod", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync("IFaceNameForMethod", 1, 2));
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(IServer.AddWithNameSubstitution), "andrew"));
    }

    [Fact]
    public async Task ExceptionControllingErrorCode()
    {
        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.ThrowRemoteInvocationException)));
        Assert.Equal(2, exception.ErrorCode);
    }

    [Fact]
    public async Task FormatterNonFatalException()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = new JsonRpc(this.serverStream);
        this.serverRpc.AddLocalRpcTarget(this.server);
        this.serverRpc.StartListening();

        ExceptionThrowingFormatter clientFormatter = new ExceptionThrowingFormatter();
        this.clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(this.clientStream, clientFormatter));
        this.clientRpc.StartListening();

        clientFormatter.ThrowException = true;
        await Assert.ThrowsAsync<Exception>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "Fail"));

        clientFormatter.ThrowException = false;
        string result = await this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "Success");
        Assert.Equal("Success!", result);
    }

    [Fact]
    public async Task FormatterFatalException()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        ExceptionThrowingFormatter serverFormatter = new ExceptionThrowingFormatter();
        this.serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(this.serverStream, serverFormatter));
        this.serverRpc.AddLocalRpcTarget(this.server);
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(this.clientStream);
        this.clientRpc.StartListening();

        // Failure on server side should cut the connection
        serverFormatter.ThrowException = true;
        await Assert.ThrowsAsync<ConnectionLostException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethod), "Fail"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.serverRpc.Dispose();
            this.clientRpc.Dispose();
            this.serverStream.Dispose();
            this.clientStream.Dispose();
        }

        if (this.serverRpc.Completion.IsFaulted)
        {
            this.Logger.WriteLine("Server faulted with: " + this.serverRpc.Completion.Exception);
        }

        base.Dispose(disposing);
    }

    protected abstract void InitializeFormattersAndHandlers();

    protected override Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated = -1, int iterations = 100, int allowedAttempts = 10)
    {
        // Make sure we aren't logging anything but errors.
        this.serverRpc.TraceSource.Switch.Level = SourceLevels.Error;
        this.clientRpc.TraceSource.Switch.Level = SourceLevels.Error;

        return base.CheckGCPressureAsync(scenario, maxBytesAllocated, iterations, allowedAttempts);
    }

    private static void SendObject(Stream receivingStream, object jsonObject)
    {
        Requires.NotNull(receivingStream, nameof(receivingStream));
        Requires.NotNull(jsonObject, nameof(jsonObject));

        string json = JsonConvert.SerializeObject(jsonObject);
        string header = $"Content-Length: {json.Length}\r\n\r\n";
        byte[] buffer = Encoding.ASCII.GetBytes(header + json);
        receivingStream.Write(buffer, 0, buffer.Length);
    }

    private void ReinitializeRpcWithoutListening()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers();

        this.serverRpc = new JsonRpc(this.serverMessageHandler, this.server);
        this.clientRpc = new JsonRpc(this.clientMessageHandler);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
    }

    private void StartListening()
    {
        this.serverRpc.StartListening();
        this.clientRpc.StartListening();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> InvokeWithProgressParameter_NoMemoryLeakConfirm_Helper()
    {
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(report => { });

        WeakReference weakRef = new WeakReference(progress);

        int invokeTask = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressParameter), new { p = progress });

        await progress.WaitAsync();

        return weakRef;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> NotifyAsyncWithProgressParameter_NoMemoryLeakConfirm_Helper()
    {
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(report => { });

        WeakReference weakRef = new WeakReference(progress);

        await Assert.ThrowsAsync<NotSupportedException>(() => this.clientRpc.NotifyAsync(nameof(Server.MethodWithProgressParameter), new { p = progress }));

        await progress.WaitAsync();

        return weakRef;
    }

    private async Task InvokeMethodWithProgressParameter(IProgress<int> progress)
    {
        await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressParameter), new { p = progress }, this.TimeoutToken);
    }

    public class BaseClass
    {
        protected readonly TaskCompletionSource<string> notificationTcs = new TaskCompletionSource<string>();

        public string BaseMethod() => "base";

        public virtual string VirtualBaseMethod() => "base";

        public string RedeclaredBaseMethod() => "base";
    }

    public class Server : BaseClass, IServer
    {
        internal const string ThrowAfterCancellationMessage = "Throw after cancellation";

        public bool NullPassed { get; private set; }

        public AsyncAutoResetEvent AllowServerMethodToReturn { get; } = new AsyncAutoResetEvent();

        public AsyncAutoResetEvent ServerMethodReached { get; } = new AsyncAutoResetEvent();

        public TaskCompletionSource<object?> ServerMethodCompleted { get; } = new TaskCompletionSource<object?>();

        public Task<string> NotificationReceived => this.notificationTcs.Task;

        public bool DelayAsyncMethodWithCancellation { get; set; }

        public static string ServerMethod(string argument)
        {
            return argument + "!";
        }

        public static string TestParameter(JToken token)
        {
            return "object " + token.ToString();
        }

        public static string TestParameter()
        {
            return "object or array";
        }

        public static string TestInvalidMethod(string test)
        {
            return "string";
        }

        public static string TestInvalidMethod(JToken test)
        {
            return "JToken";
        }

        public static int MethodWithDefaultParameter(int x, int y = 10)
        {
            return x + y;
        }

        public static int MethodWithOneNonObjectParameter(int x)
        {
            return x;
        }

        [JsonRpcMethod("test/MethodWithSingleObjectParameter", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameter(XAndYFields fields)
        {
            return fields.x + fields.y;
        }

        public static int MethodWithSingleObjectParameterWithoutDeserializationProperty(XAndYFields fields)
        {
            return fields.x + fields.y;
        }

        [JsonRpcMethod("test/MethodWithSingleObjectParameterVAndW", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterVAndW(VAndWFields fields)
        {
            return fields.v + fields.w;
        }

        [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterAndCancellationToken(XAndYFields fields, CancellationToken token)
        {
            return fields.x + fields.y;
        }

        [JsonRpcMethod("test/MethodWithSingleObjectParameterWithProgress", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterWithProgress(XAndYFieldsWithProgress fields)
        {
            fields.p!.Report(fields.x + fields.y);
            return fields.x + fields.y;
        }

        [JsonRpcMethod("test/MethodWithObjectAndExtraParameters", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithObjectAndExtraParameters(XAndYFields fields, int anotherParameter)
        {
            return fields.x + fields.y + anotherParameter;
        }

        public static int MethodWithProgressParameter(IProgress<int> p)
        {
            p.Report(1);
            return 1;
        }

        public static int MethodWithProgressAndMoreParameters(IProgress<int> p, int x, int y = 10)
        {
            int sum = x + y;
            p.Report(x);
            p.Report(y);
            return sum;
        }

        public static int MethodWithProgressArrayParameter(params IProgress<int>[] progressArray)
        {
            int report = 0;

            foreach (IProgress<int> progress in progressArray)
            {
                report++;
                progress.Report(report);
            }

            return 0;
        }

        public static int MethodWithInvalidProgressParameter(Progress<int> p)
        {
            return 1;
        }

        public int InstanceMethodWithSingleObjectParameterAndCancellationToken(XAndYFields fields, CancellationToken token)
        {
            return fields.x + fields.y;
        }

        public int? MethodReturnsNullableInt(int a) => a > 0 ? (int?)a : null;

        public int MethodAcceptsNullableArgs(int? a, int? b) => (a.HasValue ? 1 : 0) + (b.HasValue ? 1 : 0);

        public string ServerMethodInstance(string argument) => argument + "!";

        public override string VirtualBaseMethod() => "child";

        public new string RedeclaredBaseMethod() => "child";

        public Task<int> ServerMethodThatReturnsCustomTask()
        {
            var result = new CustomTask<int>(CustomTaskResult);
            result.Start();
            return result;
        }

        public async Task ServerMethodThatReturnsTask()
        {
            await Task.Yield();
        }

        public Task ServerMethodThatReturnsCancelledTask()
        {
            var tcs = new TaskCompletionSource<object>();
            tcs.SetCanceled();
            return tcs.Task;
        }

        public Task ReturnPlainTask() => Task.CompletedTask;

        public ValueTask ReturnPlainValueTaskNoYield() => default;

        public ValueTask<int> AddValueTaskNoYield(int a, int b) => new ValueTask<int>(a + b);

        public async ValueTask ReturnPlainValueTaskWithYield()
        {
            await this.AllowServerMethodToReturn.WaitAsync();
        }

        public async ValueTask<int> AddValueTaskWithYield(int a, int b)
        {
            await Task.Yield();
            return a + b;
        }

        public void MethodThatThrowsUnauthorizedAccessException()
        {
            throw new UnauthorizedAccessException();
        }

        public Foo MethodThatAcceptsFoo(Foo foo)
        {
            return new Foo
            {
                Bar = foo.Bar + "!",
                Bazz = foo.Bazz + 1,
            };
        }

        public object? MethodThatAcceptsNothingAndReturnsNull()
        {
            return null;
        }

        public object? MethodThatAccceptsAndReturnsNull(object value)
        {
            this.NullPassed = value == null;
            return null;
        }

        public void NotificationMethod(string arg)
        {
            this.notificationTcs.SetResult(arg);
        }

        public UnserializableType RepeatSpecialType(UnserializableType value)
        {
            return new UnserializableType { Value = value.Value + "!" };
        }

        public string ExpectEncodedA(string arg)
        {
            Assert.Equal("YQ==", arg);
            return arg;
        }

        public string RepeatString(string arg) => arg;

        public async Task<string> AsyncMethod(string arg)
        {
            await Task.Yield();
            return arg + "!";
        }

        public async Task<int> AsyncMethodWithCancellationAndNoArgs(CancellationToken cancellationToken)
        {
            await Task.Yield();
            return 5;
        }

        public void SyncMethodWaitsToReturn()
        {
            // Get in line for the signal before signaling the test to let us return.
            // That way, the MultipleSyncMethodsExecuteConcurrentlyOnServer test won't signal the Auto-style reset event twice
            // before both folks are waiting for it, causing a signal to be lost and the test to hang.
            Task waitToReturn = this.AllowServerMethodToReturn.WaitAsync();
            this.ServerMethodReached.Set();
            waitToReturn.Wait();
        }

        public async Task<string> AsyncMethodWithCancellation(string arg, CancellationToken cancellationToken)
        {
            try
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
            catch (Exception ex)
            {
                this.ServerMethodCompleted.TrySetException(ex);
                throw;
            }
            finally
            {
                this.ServerMethodCompleted.TrySetResult(null);
            }
        }

        public async Task<string> AsyncMethodIgnoresCancellation(string arg, CancellationToken cancellationToken)
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

            return arg + "!";
        }

        public async Task<string> AsyncMethodWithJTokenAndCancellation(JToken paramObject, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();

            // TODO: remove when https://github.com/Microsoft/vs-threading/issues/185 is fixed
            if (this.DelayAsyncMethodWithCancellation)
            {
                await Task.Delay(UnexpectedTimeout).WithCancellation(cancellationToken);
            }

            await this.AllowServerMethodToReturn.WaitAsync(cancellationToken);
            return paramObject.ToString(Formatting.None) + "!";
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

        public async Task AsyncMethodThatThrows()
        {
            await Task.Yield();
            throw new Exception();
        }

        public Task<object> MethodThatReturnsTaskOfInternalClass()
        {
            var result = new Task<object>(() => new InternalClass());
            result.Start();
            return result;
        }

        public Task<int> MethodThatEndsInAsync()
        {
            return Task.FromResult(3);
        }

        public Task<int> MethodThatMayEndInAsync()
        {
            return Task.FromResult(4);
        }

        public Task<int> MethodThatMayEndIn()
        {
            return Task.FromResult(5);
        }

        public int OverloadedMethod(Foo foo)
        {
            Assert.NotNull(foo);
            return 1;
        }

        public int OverloadedMethod(int i)
        {
            return i;
        }

        public int MethodWithOutParameter(out int i)
        {
            i = 1;
            return 1;
        }

        public void MethodWithRefParameter(ref int i)
        {
            i = i + 1;
        }

        public string ARoseBy(string name) => name.ToUpperInvariant();

        [JsonRpcMethod("ClassNameForMethod")]
        public int AddWithNameSubstitution(int a, int b) => a + b;

        public void ThrowRemoteInvocationException()
        {
            throw new LocalRpcException { ErrorCode = 2, ErrorData = new { myCustomData = "hi" } };
        }

        internal void InternalMethod()
        {
            this.ServerMethodReached.Set();
        }

        [JsonRpcMethod]
        internal void InternalMethodWithAttribute()
        {
            this.ServerMethodReached.Set();
        }
    }

    public class AdditionalServerTargetOne
    {
        public int PlusOne(int arg)
        {
            return arg + 1;
        }
    }

    public class AdditionalServerTargetTwo
    {
        public int PlusOne(int arg)
        {
            return arg + 3;
        }

        public string PlusOne(string arg)
        {
            return arg + " plus one!";
        }

        public int PlusTwo(int arg)
        {
            return arg + 2;
        }
    }

    [DataContract]
    public class Foo
    {
        [DataMember(Order = 0, IsRequired = true)]
        public string? Bar { get; set; }

        [DataMember(Order = 1)]
        public int Bazz { get; set; }
    }

    public class UnserializableType
    {
        [JsonIgnore]
        public string? Value { get; set; }
    }

    public class UnserializableTypeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(UnserializableType);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return new UnserializableType
            {
                Value = (string)reader.Value,
            };
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(((UnserializableType)value).Value);
        }
    }

    [DataContract]
    public class XAndYFields
    {
        // We disable SA1307 because we must use lowercase members as required to match the parameter names.
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
        [DataMember]
        public int x;
        [DataMember]
        public int y;
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter
    }

    [DataContract]
    public class VAndWFields
    {
        // We disable SA1307 because we must use lowercase members as required to match the parameter names.
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
        [DataMember]
        public int v;
        [DataMember]
        public int w;
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter
    }

    [DataContract]
    public class XAndYFieldsWithProgress
    {
        // We disable SA1307 because we must use lowercase members as required to match the parameter names.
#pragma warning disable SA1307 // Accessible fields should begin with upper-case letter
        [DataMember]
        public int x;
        [DataMember]
        public int y;
        [DataMember]
        public IProgress<int>? p;
#pragma warning restore SA1307 // Accessible fields should begin with upper-case letter
    }

    internal class InternalClass
    {
    }

    /// <summary>
    /// This emulates what .NET Core 2.1 does where async <see cref="Task{T}"/> methods actually return an instance of a private derived type.
    /// </summary>
    private class CustomTask<T> : Task<T>
    {
        public CustomTask(T result)
            : base(() => result)
        {
        }
    }

    private class ServerSynchronizationContext : SynchronizationContext
    {
        private ThreadLocal<int> runningInContext = new ThreadLocal<int>();

        /// <summary>
        /// Gets a value indicating whether the caller is running on top of this instance
        /// somewhere lower on the callstack.
        /// </summary>
        internal bool RunningInContext => this.runningInContext.Value > 0;

        public override void Send(SendOrPostCallback d, object state)
        {
            throw new NotImplementedException();
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            Task.Run(() =>
            {
                this.runningInContext.Value++;
                try
                {
                    d(state);
                }
                finally
                {
                    this.runningInContext.Value--;
                }
            });
        }
    }

    private class BlockingPostSynchronizationContext : SynchronizationContext
    {
        private long postCalls;

        internal ManualResetEventSlim AllowPostToReturn { get; } = new ManualResetEventSlim(false);

        internal AsyncManualResetEvent PostInvoked { get; } = new AsyncManualResetEvent();

        internal long PostCalls => Interlocked.Read(ref this.postCalls);

        public override void Post(SendOrPostCallback d, object state)
        {
            Interlocked.Increment(ref this.postCalls);
            this.PostInvoked.Set();
            this.AllowPostToReturn.Wait();
            base.Post(d, state);
        }
    }

    private class ExceptionThrowingFormatter : JsonMessageFormatter, IJsonRpcMessageFormatter
    {
        public bool ThrowException;

        public new void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
        {
            if (this.ThrowException)
            {
                throw new Exception("Non fatal exception...");
            }

            base.Serialize(bufferWriter, message);
        }
    }
}
