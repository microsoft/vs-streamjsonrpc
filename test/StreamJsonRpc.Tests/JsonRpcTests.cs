// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using JsonNET = Newtonsoft.Json;
using STJ = System.Text.Json.Serialization;

public abstract partial class JsonRpcTests : TestBase
{
#pragma warning disable SA1310 // Field names should not contain underscore
    protected const int COR_E_UNAUTHORIZEDACCESS = unchecked((int)0x80070005);
#pragma warning restore SA1310 // Field names should not contain underscore

    protected readonly Server server;
    protected Stream serverStream;
    protected JsonRpc serverRpc;
    protected IJsonRpcMessageHandler serverMessageHandler;
    protected IJsonRpcMessageFormatter serverMessageFormatter;
    protected CollectingTraceListener serverTraces;

    protected Stream clientStream;
    protected JsonRpc clientRpc;
    protected IJsonRpcMessageHandler clientMessageHandler;
    protected IJsonRpcMessageFormatter clientMessageFormatter;
    protected CollectingTraceListener clientTraces;

    private const int CustomTaskResult = 100;

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
    public JsonRpcTests(ITestOutputHelper logger)
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        : base(logger)
    {
        TaskCompletionSource<JsonRpc> serverRpcTcs = new TaskCompletionSource<JsonRpc>();

        this.server = new Server();

        this.ReinitializeRpcWithoutListening();
        Assumes.NotNull(this.serverRpc);
        Assumes.NotNull(this.clientRpc);

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();
    }

    public interface IServerWithIgnoredMethod
    {
        [JsonRpcIgnore]
        void IgnoredMethod();
    }

    protected interface IControlledFlushHandler : IJsonRpcMessageHandler
    {
        /// <summary>
        /// Gets an event that is raised when <see cref="MessageHandlerBase.FlushAsync(CancellationToken)"/> is invoked.
        /// </summary>
        AsyncAutoResetEvent FlushEntered { get; }

        /// <summary>
        /// Gets an event that must be set before <see cref="MessageHandlerBase.FlushAsync(CancellationToken)"/> is allowed to return.
        /// </summary>
        AsyncManualResetEvent AllowFlushAsyncExit { get; }
    }

    private interface IServer
    {
        [JsonRpcMethod("AnotherName")]
        string ARoseBy(string name);

        [JsonRpcMethod("IFaceNameForMethod")]
        int AddWithNameSubstitution(int a, int b);

        [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
        int InstanceMethodWithSingleObjectParameterAndCancellationToken(XAndYProperties fields, CancellationToken token);

        int InstanceMethodWithSingleObjectParameterButNoAttribute(XAndYProperties fields);

        int Add_ExplicitInterfaceImplementation(int a, int b);

        [JsonRpcIgnore]
        void InterfaceIgnoredMethod();
    }

    private interface IServerDerived : IServer
    {
        bool MethodOnDerived();
    }

    protected bool IsTypeNameHandlingEnabled => this.clientMessageFormatter is JsonMessageFormatter { JsonSerializer: { TypeNameHandling: JsonNET.TypeNameHandling.Objects } };

    protected abstract Type FormatterExceptionType { get; }

    [Fact]
    public async Task AddLocalRpcTarget_OfT_InterfaceOnly()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers();

        this.serverRpc = new JsonRpc(this.serverMessageHandler);
        this.serverRpc.AddLocalRpcTarget<IServerDerived>(this.server, null);
        this.clientRpc = new JsonRpc(this.clientMessageHandler);

        this.AddTracing();

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        // Verify that members on the interface and base interfaces are callable.
        await this.clientRpc.InvokeAsync("AnotherName", new object[] { "my -name" }).WithCancellation(this.TimeoutToken);
        await this.clientRpc.InvokeAsync(nameof(IServerDerived.MethodOnDerived)).WithCancellation(this.TimeoutToken);

        // Verify that explicitly interface implementations of members on the interface are callable.
        Assert.Equal(3, await this.clientRpc.InvokeAsync<int>(nameof(IServer.Add_ExplicitInterfaceImplementation), 1, 2).WithCancellation(this.TimeoutToken));

        // Verify that members NOT on the interface are not callable, whether public or internal.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.AsyncMethod), new object[] { "my-name" })).WithCancellation(this.TimeoutToken);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.InternalMethod))).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task AddLocalRpcTarget_OfT_ActualClass()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers();

        this.serverRpc = new JsonRpc(this.serverMessageHandler);
        this.serverRpc.AddLocalRpcTarget<Server>(this.server, null);
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(this.clientMessageHandler);
        this.clientRpc.StartListening();

        // Verify that public members on the class (and NOT the interface) are callable.
        await this.clientRpc.InvokeAsync(nameof(Server.AsyncMethod), new object[] { "my-name" });
        await this.clientRpc.InvokeAsync(nameof(BaseClass.BaseMethod));

        // Verify that explicitly interface implementations of members on the interface are NOT callable.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<int>(nameof(IServer.Add_ExplicitInterfaceImplementation), 1, 2));

        // Verify that internal members on the class are NOT callable.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.InternalMethod)));
    }

    [Fact]
    public async Task AddLocalRpcTarget_OfT_ActualClass_NonPublicAccessible()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers();

        this.serverRpc = new JsonRpc(this.serverMessageHandler);
        this.serverRpc.AddLocalRpcTarget<Server>(this.server, new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(this.clientMessageHandler);
        this.clientRpc.StartListening();

        // Verify that public members on the class (and NOT the interface) are callable.
        await this.clientRpc.InvokeAsync(nameof(Server.AsyncMethod), new object[] { "my-name" });

        // Verify that explicitly interface implementations of members on the interface are callable.
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<int>(nameof(IServer.Add_ExplicitInterfaceImplementation), 1, 2));

        // Verify that internal members on the class are callable.
        await this.clientRpc.InvokeAsync(nameof(Server.InternalMethod));
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
        await disconnected.WaitAsync(TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);

        // The method should not have been invoked.
        Assert.False(server.NullPassed);
    }

    [Fact]
    public async Task Attach_NullReceivingStream_CanOnlySendNotifications()
    {
        var sendingStream = new SimplexStream();
        var rpc = JsonRpc.Attach(sendingStream: sendingStream, receivingStream: null);

        // Sending notifications is fine, as it's an outbound-only communication.
        await rpc.NotifyAsync("foo");

        // Verify that something was sent.
        await sendingStream.ReadAsync(new byte[1], 0, 1, TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);

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
    public void ExceptionStrategy_DefaultValue()
    {
        ExceptionProcessing originalValue = this.clientRpc.ExceptionStrategy;
        Assert.Equal(ExceptionProcessing.CommonErrorData, originalValue);
    }

    [Fact]
    public void ExceptionStrategy_ThrowsOnLockedConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable);
        Assert.Equal(ExceptionProcessing.CommonErrorData, this.clientRpc.ExceptionStrategy);
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.ExceptionStrategy = ExceptionProcessing.CommonErrorData);
        Assert.Equal(ExceptionProcessing.CommonErrorData, this.clientRpc.ExceptionStrategy);

        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        Assert.Equal(ExceptionProcessing.ISerializable, this.clientRpc.ExceptionStrategy);
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
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task InvokeWithCancellationAsync_ServerMethodSelfCancelsDoesNotReportWithOurToken()
    {
        var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ServerMethodThatReturnsCancelledTask), cancellationToken: cts.Token));
        Assert.Equal(CancellationToken.None, ex.CancellationToken);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task CanInvokeMethodThatReturnsTaskOfInternalClass()
    {
        Assert.SkipWhen(this is JsonRpcPolyTypeJsonHeadersTests, "Not (yet) supported.");

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

    [Theory, PairwiseData]
    public async Task CanCallAsyncMethodThatThrows(ExceptionProcessing exceptionStrategy)
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = exceptionStrategy;
        this.serverRpc.ExceptionStrategy = exceptionStrategy;

        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrows)));
        var errorData = Assert.IsType<CommonErrorData>(exception.DeserializedErrorData);
        Assert.Equal(Server.ExceptionMessage, errorData.Message);

        if (exceptionStrategy == ExceptionProcessing.ISerializable)
        {
            Exception inner = Assert.IsType<Exception>(exception.InnerException);
            Assert.Equal(Server.ExceptionMessage, inner.Message);
        }
    }

    [Theory, PairwiseData]
    public async Task CanCallAsyncMethodThatThrowsNonSerializableException(ExceptionProcessing exceptionStrategy)
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = exceptionStrategy;
        this.serverRpc.ExceptionStrategy = exceptionStrategy;

        RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrowsNonSerializableException)));
        var errorData = Assert.IsType<CommonErrorData>(exception.DeserializedErrorData);
        Assert.Equal(Server.ExceptionMessage, errorData.Message);

        if (exceptionStrategy == ExceptionProcessing.ISerializable)
        {
            Assert.Null(exception.InnerException);

            // Assert that the server logged a warning about the exception problem.
            Assert.Contains(JsonRpc.TraceEvents.ExceptionNotSerializable, this.serverTraces.Ids);
        }
    }

    [Theory, PairwiseData]
    public async Task CanCallAsyncMethodThatThrowsExceptionWithoutDeserializingConstructor(ExceptionProcessing exceptionStrategy)
    {
        Assert.SkipWhen(this is JsonRpcPolyTypeJsonHeadersTests, "Not (yet) supported.");

        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = exceptionStrategy;
        this.serverRpc.ExceptionStrategy = exceptionStrategy;
        this.clientRpc.LoadableTypes.Add(typeof(ExceptionMissingDeserializingConstructor));

        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrowsAnExceptionWithoutDeserializingConstructor)));
        var errorData = Assert.IsType<CommonErrorData>(exception.DeserializedErrorData);
        Assert.Equal(Server.ExceptionMessage, errorData.Message);

        if (exceptionStrategy == ExceptionProcessing.ISerializable)
        {
            // The inner exception type should be the nearest base type that declares the deserializing constructor.
            Exception inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal(Server.ExceptionMessage, inner.Message);
        }

        if (exceptionStrategy == ExceptionProcessing.ISerializable)
        {
            Assert.Contains(JsonRpc.TraceEvents.ExceptionNotDeserializable, this.clientTraces.Ids);
        }
    }

    [Fact]
    public async Task CanCallAsyncMethodThatThrowsExceptionWhileSerializingException()
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.serverRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;

        RemoteSerializationException exception = await Assert.ThrowsAnyAsync<RemoteSerializationException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.AsyncMethodThatThrowsExceptionThatThrowsOnSerialization)));
        Assert.Equal(JsonRpcErrorCode.ResponseSerializationFailure, exception.ErrorCode);
        Assert.NotEqual(Server.ExceptionMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ThrowCustomExceptionThatImplementsISerializableProperly()
    {
        Assert.SkipWhen(this is JsonRpcPolyTypeJsonHeadersTests, "Not (yet) supported.");

        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.serverRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.clientRpc.LoadableTypes.Add(typeof(PrivateSerializableException));

        RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.ThrowPrivateSerializableException)));
        Assert.IsType<PrivateSerializableException>(exception.InnerException);
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
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.OverloadedMethod), new XAndYProperties { x = 100 }));
    }

    [Fact]
    public async Task ThrowsIfTargetNotSet()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.serverRpc.InvokeAsync(nameof(Server.OverloadedMethod)));
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")] // Test showing unstability on Azure Pipelines, but always succeeds locally.
    [Trait("FailsOnMono", "true")]
    public async Task InvokeWithProgressParameter_NoMemoryLeakConfirm()
    {
        Assert.SkipWhen(IsRunningUnderLiveUnitTest, "Live Unit Testing");
        WeakReference weakRef = await this.InvokeWithProgressParameter_NoMemoryLeakConfirm_Helper();
        GC.Collect();
        Assert.False(weakRef.IsAlive);
    }

    [Fact]
    [Trait("FailsOnMono", "true")]
    public async Task NotifyWithProgressParameter_NoMemoryLeakConfirm()
    {
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
        this.serverRpc.Disconnected += (object? sender, JsonRpcDisconnectedEventArgs e) =>
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
        this.serverRpc.Disconnected += (object? sender, JsonRpcDisconnectedEventArgs e) =>
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
    public async Task DisposeInDisconnectedHandler()
    {
        var eventHandlerResultSource = new TaskCompletionSource<object?>();
        this.serverRpc.Disconnected += (s, e) =>
        {
            try
            {
                this.serverRpc.Dispose();
                eventHandlerResultSource.SetResult(null);
            }
            catch (Exception ex)
            {
                eventHandlerResultSource.SetException(ex);
            }
        };

        this.clientRpc.Dispose();
        await eventHandlerResultSource.Task.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task DisposeDuringOutboundCall()
    {
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.AsyncMethodWithCancellation), new object?[] { "hi" }, this.TimeoutToken);
        await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
        this.clientRpc.Dispose();
        await Assert.ThrowsAsync<ConnectionLostException>(() => invokeTask);
    }

    [Fact]
    public async Task DisposeDuringCanceledOutboundCall()
    {
        var cts = new CancellationTokenSource();
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ReturnPlainValueTaskWithYield), cancellationToken: cts.Token);
        cts.Cancel();
        this.clientRpc.Dispose();
        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask);
        Assert.Equal(cts.Token, oce.CancellationToken);
        Assert.Null(oce.InnerException);
    }

    [Fact]
    public async Task ConnectionLostDuringCanceledOutboundCall()
    {
        var cts = new CancellationTokenSource();
        Task invokeTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ReturnPlainValueTaskWithYield), cancellationToken: cts.Token);
        cts.Cancel();
        this.serverRpc.Dispose();
        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask);
        Assert.Equal(cts.Token, oce.CancellationToken);
        Assert.Null(oce.InnerException);
    }

    [Fact]
    public async Task ConnectionLostDuringCanceledCallback()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;
        this.server.Tests = this;

        this.clientRpc.AllowModificationWhileListening = true;
        string testCallbackMethod = $"{nameof(this.ConnectionLostDuringCanceledCallback)}_Callback";
        var callbackReached = new AsyncManualResetEvent();
        var releaseCallback = new AsyncManualResetEvent();
        this.clientRpc.AddLocalRpcMethod(testCallbackMethod, new Func<Task>(async delegate
        {
            callbackReached.Set();
            await releaseCallback.WaitAsync(this.TimeoutToken);
        }));

        Task invocationTask = this.clientRpc.InvokeWithCancellationAsync(nameof(Server.Callback), new object?[] { testCallbackMethod }, this.TimeoutToken);
        await callbackReached.WaitAsync(this.TimeoutToken);

        this.clientRpc.Dispose();
        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.server.ServerMethodCompleted.Task).WithCancellation(this.TimeoutToken);
        Assert.Null(oce.InnerException);
    }

    /// <summary>
    /// This test verifies that the transmitting side and the receiving side both respond with typical connection lost paths
    /// even when transmission/receiving is active at time of loss.
    /// </summary>
    [Fact]
    public async Task ConnectionLostDuringMessageTransmission()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();

        this.serverStream = streams.Item1;
        var clientSlowWriter = new SlowWriteStream(streams.Item2);
        this.clientStream = clientSlowWriter;

        this.InitializeFormattersAndHandlers();

        this.serverRpc = new JsonRpc(this.serverMessageHandler, this.server);
        this.clientRpc = new JsonRpc(this.clientMessageHandler);
        this.clientRpc.StartListening();
        this.serverRpc.StartListening();

        Task invokingTask = this.clientRpc.InvokeAsync("somemethod");
        await clientSlowWriter.WriteEntered.WaitAsync(this.TimeoutToken);
        this.serverRpc.Dispose();

        await Assert.ThrowsAsync<ConnectionLostException>(() => invokingTask).WithCancellation(this.TimeoutToken);
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
    [Trait("JsonRpcIgnore", "")]
    public async Task PublicIgnoredMethodCannotBeInvoked()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.PublicIgnoredMethod), cancellationToken: this.TimeoutToken));
    }

    [Fact]
    [Trait("JsonRpcIgnore", "")]
    public async Task PublicMethodIgnoredByInterfaceCannotBeInvoked()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.InterfaceIgnoredMethod), cancellationToken: this.TimeoutToken));
    }

    [Fact]
    [Trait("JsonRpcIgnore", "")]
    public async Task IgnoredMethodCanBeInvokedByClientWhenAddedExplicitly()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcMethod(typeof(Server).GetMethod(nameof(Server.PublicIgnoredMethod))!, this.server, null);
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.PublicIgnoredMethod), cancellationToken: this.TimeoutToken);
    }

    [Fact]
    [Trait("JsonRpcIgnore", "")]
    public async Task InternalIgnoredMethodCannotBeInvoked()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = new JsonRpc(this.serverStream);
        this.clientRpc = new JsonRpc(this.clientStream);

        this.serverRpc.AddLocalRpcTarget(this.server, new JsonRpcTargetOptions { AllowNonPublicInvocation = true });

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.InternalIgnoredMethod), cancellationToken: this.TimeoutToken));
    }

    [Fact]
    [Trait("JsonRpcIgnore", "")]
    public void ConflictedIgnoreMethodThrows()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.serverRpc = new JsonRpc(this.serverStream);
        Assert.Throws<ArgumentException>(() => this.serverRpc.AddLocalRpcTarget(new ServerWithConflictingAttributes()));
    }

    [Fact]
    [Trait("JsonRpcIgnore", "")]
    public void ConflictedIgnoreMethodViaInterfaceThrows()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.serverRpc = new JsonRpc(this.serverStream);
        Assert.Throws<ArgumentException>(() => this.serverRpc.AddLocalRpcTarget(new ServerWithConflictingAttributesViaInheritance()));
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
        int? result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodReturnsNullableInt), 0).WithCancellation(this.TimeoutToken);
        Assert.Null(result);
        result = await this.clientRpc.InvokeAsync<int?>(nameof(Server.MethodReturnsNullableInt), 5).WithCancellation(this.TimeoutToken);
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
    public async Task CanCallMethodWithAttributeOmittingAsyncSuffix()
    {
        int result = await this.clientRpc.InvokeAsync<int>("MethodWithAttributeThatEndsIn");
        Assert.Equal(6, result);
    }

    [Fact]
    public async Task CanCallMethodWithAttributeWithoutOmittingAsyncSuffix()
    {
        int result = await this.clientRpc.InvokeAsync<int>(nameof(Server.MethodWithAttributeThatEndsInAsync));
        Assert.Equal(6, result);
    }

    [Fact]
    public void SynchronizationContext_DefaultIsNonConcurrent()
    {
        Assert.IsType<NonConcurrentSynchronizationContext>(this.serverRpc.SynchronizationContext);
    }

    [Fact]
    public void SynchronizationContext_CanBeCleared()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.SynchronizationContext = null;
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
        Task unblockingTask = await Task.WhenAny(invoke1, invoke2, syncContext.PostInvoked.WaitAsync(TestContext.Current.CancellationToken)).WithCancellation(UnexpectedTimeoutToken);
        await unblockingTask; // rethrow any exception that may have occurred while we were waiting.

        await Task.Delay(ExpectedTimeout, TestContext.Current.CancellationToken);
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
                ((Nerdbank.FullDuplexStream)this.clientStream).BeforeWrite = (stream, buffer, offset, count) =>
               {
                   // Cancel on the first write, when the header is being written but the content is not yet.
                   if (!cts.IsCancellationRequested)
                   {
                       cts.Cancel();
                   }
               };

                var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token)).WithTimeout(UnexpectedTimeout);
                Assert.Equal(cts.Token, ex.CancellationToken);
                Assert.Null(ex.InnerException);
                ((Nerdbank.FullDuplexStream)this.clientStream).BeforeWrite = null;
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
    public async Task Invoke_ThrowsConnectionLostExceptionOverDisposedException_ForNewRequests()
    {
        this.serverStream.Dispose();
        await this.clientRpc.Completion;
        await Assert.ThrowsAsync<ConnectionLostException>(() => this.clientRpc.InvokeAsync("Something"));
    }

    [Fact]
    public async Task InvokeAsync_CanCallCancellableMethodWithNoArgs()
    {
        Assert.Equal(5, await this.clientRpc.InvokeAsync<int>(nameof(Server.AsyncMethodWithCancellationAndNoArgs)));
    }

    [Fact]
    public async Task InvokeWithCancellationAsync_CanCallCancellableMethodWithNoArgs()
    {
        Assert.Equal(5, await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.AsyncMethodWithCancellationAndNoArgs), cancellationToken: TestContext.Current.CancellationToken));

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
    public async Task InvokeWithParameterObjectAsync_CanCallCancellableMethodImplementedSynchronously()
    {
        using var cts = new CancellationTokenSource();
        Task invocationTask = this.clientRpc.InvokeWithParameterObjectAsync(nameof(Server.SyncMethodWithCancellation), NamedArgs.Create(new { waitForCancellation = true }), cancellationToken: cts.Token);
        await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => invocationTask);
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
            Assert.Null(ex.InnerException);
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
            string result = await invokeTask.WithCancellation(this.TimeoutToken);
            Assert.Equal("a!", result);
        }
    }

    [Theory, PairwiseData]
    public async Task CancelMayStillReturnErrorFromServer(ExceptionProcessing exceptionStrategy)
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = exceptionStrategy;
        this.serverRpc.ExceptionStrategy = exceptionStrategy;

        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodFaultsAfterCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();
            this.server.AllowServerMethodToReturn.Set();
            var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => invokeTask).WithCancellation(this.TimeoutToken);
            Assert.Equal(Server.ThrowAfterCancellationMessage, ex.Message);

            if (exceptionStrategy == ExceptionProcessing.ISerializable)
            {
                var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
                Assert.Equal(Server.ThrowAfterCancellationMessage, inner.Message);
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
    public async Task InvokeThenCancelToken_BetweenWriteAndFlush()
    {
        this.ReinitializeRpcWithoutListening(controlledFlushingClient: true);
        var clientMessageHandler = (IControlledFlushHandler)this.clientMessageHandler;

        this.clientRpc.StartListening();
        this.serverRpc.StartListening();

        using (var cts = new CancellationTokenSource())
        {
            this.server.AllowServerMethodToReturn.Set();
            Task<string> invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethod), new[] { "a" }, cts.Token);
            await clientMessageHandler.FlushEntered.WaitAsync(this.TimeoutToken);
            cts.Cancel();
            clientMessageHandler.AllowFlushAsyncExit.Set();
            await invokeTask.WithCancellation(this.TimeoutToken);

            string result = await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethod), new[] { "b" }, this.TimeoutToken).WithCancellation(this.TimeoutToken);
            Assert.Equal("b!", result);
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
        await Task.Delay(ExpectedTimeout, TestContext.Current.CancellationToken);
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
        string result1 = await this.clientRpc.InvokeWithParameterObjectAsync<string>(nameof(Server.TestParameter), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("object or array", result1);
    }

    [Fact]
    public async Task InvokeWithParameterObject_Fields()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithDefaultParameter), new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithParameterObject_DefaultParameters()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithDefaultParameter), NamedArgs.Create(new { x = 2 }), this.TimeoutToken);
        Assert.Equal(12, sum);
    }

    [Fact]
    public async Task InvokeWithParameterObject_ProgressParameter()
    {
        int report = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(n => report = n);

        int result = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressParameter), NamedArgs.Create(new { p = progress }), this.TimeoutToken);

        await progress.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task InvokeWithParameterObject_ProgressParameterMultipleRequests()
    {
        int report1 = 0;
        ProgressWithCompletion<int> progress1 = new ProgressWithCompletion<int>(n => report1 = n);

        int report2 = 0;
        ProgressWithCompletion<int> progress2 = new ProgressWithCompletion<int>(n => report2 = n * 2);

        int report3 = 0;
        ProgressWithCompletion<int> progress3 = new ProgressWithCompletion<int>(n => report3 = n * 3);

        await this.InvokeMethodWithProgressParameter(progress1);
        await this.InvokeMethodWithProgressParameter(progress2);
        await this.InvokeMethodWithProgressParameter(progress3);

        await progress1.WaitAsync(TestContext.Current.CancellationToken);
        await progress2.WaitAsync(TestContext.Current.CancellationToken);
        await progress3.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report1);
        Assert.Equal(2, report2);
        Assert.Equal(3, report3);
    }

    [Fact]
    public async Task InvokeWithParameterObject_Progress_InvalidParamMethod()
    {
        int report = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(n => report = n);

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithInvalidProgressParameter), NamedArgs.Create(new { p = progress }), this.TimeoutToken));
    }

    /// <summary>
    /// Asserts that an outbound RPC call's Task will not complete before all inbound progress updates have been invoked.
    /// </summary>
    /// <remarks>
    /// This is important so that <see cref="ProgressWithCompletion{T}.WaitAsync()"/> is guaranteed to wait for all
    /// progress updates that were made by the RPC server so the client doesn't get updates later than expected.
    /// </remarks>>
    [Fact]
    public async Task ProgressParameterHasStableCompletionRelativeToRpcTask()
    {
        int? received = null;
        ManualResetEventSlim evt = new();
        ControlledProgress<int> progress = new(n =>
        {
            evt.Wait();
            received = n;
        });

        Task<int> result = this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.MethodWithProgressParameter), [progress], [typeof(IProgress<int>)], this.TimeoutToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => result.WithCancellation(ExpectedTimeoutToken));
        evt.Set();
        await result.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task ReportProgressWithUnserializableData_LeavesTraceEvidence()
    {
        var progress = new Progress<TypeThrowsWhenSerialized>();
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.MethodWithUnserializableProgressType), new object[] { progress }, [typeof(IProgress<TypeThrowsWhenSerialized>)], cancellationToken: this.TimeoutToken);

        // Verify that the trace explains what went wrong with the original exception message.
        while (!this.serverTraces.Messages.Any(m => m.Contains("Can't touch this")))
        {
            await this.serverTraces.MessageReceived.WaitAsync(this.TimeoutToken);
        }
    }

    [Fact]
    public async Task NotifyAsync_LeavesTraceEvidenceOnFailure()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyAsync("DoesNotMatter", new TypeThrowsWhenSerialized()));
        Assert.IsAssignableFrom(this.FormatterExceptionType, exception);

        // Verify that the trace explains what went wrong with the original exception message.
        while (!this.clientTraces.Messages.Any(m => m.Contains("Can't touch this")))
        {
            await this.clientTraces.MessageReceived.WaitAsync(this.TimeoutToken);
        }
    }

    [Fact]
    public async Task InvokeWithParameterObject_ProgressParameterAndFields()
    {
        int report = 0;
        var progress = new ProgressWithCompletion<int>(n => Interlocked.Add(ref report, n));

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressAndMoreParameters), NamedArgs.Create(new { p = progress, x = 2, y = 5 }), this.TimeoutToken);

        await progress.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, report);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithParameterObject_ProgressAndDefaultParameters()
    {
        int report = 0;
        var progress = new ProgressWithCompletion<int>(n => Interlocked.Add(ref report, n));

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressAndMoreParameters), NamedArgs.Create(new { p = progress, x = 2 }), this.TimeoutToken);

        await progress.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, report);
        Assert.Equal(12, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_SendingExpectedObject()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithSingleObjectParameter", new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_ServerMethodExpectsObjectButDoesNotSetDeserializationProperty()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(async () => await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithSingleObjectParameterWithoutDeserializationProperty), new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken));
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_ServerMethodExpectsObjectButSendingDifferentType()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithSingleObjectParameterVAndW", new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken);

        Assert.Equal(0, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_ServerMethodSetDeserializationPropertyButExpectMoreThanOneParameter()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(async () => await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithObjectAndExtraParameters", new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken));
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_SendingExpectedObjectAndCancellationToken()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithSingleObjectParameterAndCancellationToken), new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_SupplyNoArgument()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithSingleObjectParameterWithDefaultValue), cancellationToken: this.TimeoutToken);
        Assert.Equal(-1, sum);
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_SendingExpectedObjectAndCancellationToken_InterfaceMethodAttributed()
    {
        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(IServer.InstanceMethodWithSingleObjectParameterAndCancellationToken), new XAndYProperties { x = 2, y = 5 }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task SerializationFailureInResult_ThrowsToClient()
    {
        var ex = await Assert.ThrowsAsync<RemoteSerializationException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.GetUnserializableType), cancellationToken: this.TimeoutToken));
        Assert.Equal(JsonRpcErrorCode.ResponseSerializationFailure, ex.ErrorCode);
    }

    [Fact]
    public async Task AddLocalRpcTarget_UseSingleObjectParameterDeserialization()
    {
        var streams = FullDuplexStream.CreatePair();
        var rpc = new JsonRpc(streams.Item1, streams.Item2)
        {
            TraceSource = new TraceSource("Loopback", SourceLevels.Verbose)
            {
                Listeners = { new XunitTraceListener(this.Logger) },
            },
        };
        rpc.AddLocalRpcTarget(new Server(), new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        rpc.StartListening();

        Assert.Equal(3, await rpc.InvokeWithParameterObjectAsync<int>(nameof(IServer.InstanceMethodWithSingleObjectParameterButNoAttribute), new XAndYProperties { x = 1, y = 2 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeWithSingleObjectParameter_SendingWithProgressProperty()
    {
        int report = 0;
        var progress = new ProgressWithCompletion<int>(n => Interlocked.Add(ref report, n));

        int sum = await this.clientRpc.InvokeWithParameterObjectAsync<int>("test/MethodWithSingleObjectParameterWithProgress", new XAndYPropertiesWithProgress { x = 2, y = 5, p = progress }, this.TimeoutToken);

        await progress.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, report);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithArrayParameters_SendingWithProgressProperty()
    {
        int report = 0;
        var progress = new ProgressWithCompletion<int>(n => Interlocked.Add(ref report, n));

        int sum = await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.MethodWithParameterContainingIProgress), new object[] { new XAndYPropertiesWithProgress { x = 2, y = 5, p = progress } }, this.TimeoutToken);

        await progress.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithArrayParameters_SendingWithProgressConcreteTypeProperty()
    {
        Assert.SkipWhen(this.IsTypeNameHandlingEnabled, "This test substitutes types in a way that strong-typing across RPC is incompatible.");
        int report = 0;
        var progress = new ProgressWithCompletion<int>(n => Interlocked.Add(ref report, n));

        int sum = await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.MethodWithParameterContainingIProgress), new object[] { new StrongTypedProgressType { x = 2, y = 5, p = progress } }, this.TimeoutToken);

        await progress.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, report);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithArrayParameters_SendingWithNullProgressProperty()
    {
        int sum = await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.MethodWithParameterContainingIProgress), new object[] { new XAndYPropertiesWithProgress { x = 2, y = 5 } }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task InvokeWithArrayParameters_SendingWithNullProgressConcreteTypeProperty()
    {
        Assert.SkipWhen(this.IsTypeNameHandlingEnabled, "This test substitutes types in a way that strong-typing across RPC is incompatible.");
        int sum = await this.clientRpc.InvokeWithCancellationAsync<int>(nameof(Server.MethodWithParameterContainingIProgress), new object[] { new StrongTypedProgressType { x = 2, y = 5 } }, this.TimeoutToken);
        Assert.Equal(7, sum);
    }

    [Fact]
    public async Task Invoke_MultipleProgressArguments()
    {
        bool progress1Reported = false;
        bool progress2Reported = false;

        var progress1 = new ProgressWithCompletion<int>(n => progress1Reported = true);
        var progress2 = new ProgressWithCompletion<int>(n => progress2Reported = true);

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.MethodWithMultipleProgressParameters), new object[] { progress1, progress2 }, this.TimeoutToken);

        await progress1.WaitAsync(this.TimeoutToken);
        Assert.True(progress1Reported);
        await progress2.WaitAsync(this.TimeoutToken);
        Assert.True(progress2Reported);
    }

    [Fact]
    public async Task CustomSerializedType_WorksWithConverter()
    {
        var result = await this.clientRpc.InvokeAsync<CustomSerializedType>(nameof(this.server.RepeatSpecialType), new CustomSerializedType { Value = "a" });
        Assert.Equal("a!", result.Value);
    }

    [Fact]
    public async Task ProgressOfT_WithCustomSerializedTypeArgument()
    {
        const string ExpectedResult = "hi!";
        string? actualResult = null;
        var progress = new ProgressWithCompletion<CustomSerializedType>(v => actualResult = v.Value);
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.RepeatSpecialType_ViaProgress), new object?[] { ExpectedResult.TrimEnd('!'), progress }, this.TimeoutToken);
        await progress.WaitAsync(this.TimeoutToken);
        Assert.Equal(ExpectedResult, actualResult);
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
        await this.server.ServerMethodReached.WaitAsync(TestContext.Current.CancellationToken);
        this.clientStream.Dispose();
        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.server.ServerMethodCompleted.Task).WithCancellation(this.TimeoutToken);
        Assert.Null(oce.InnerException);
    }

    [Fact]
    public async Task ServerMethodIsNotCanceledWhenConnectionDrops()
    {
        Assert.False(this.serverRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed);
        Task rpcTask = this.clientRpc.InvokeAsync(nameof(Server.AsyncMethodWithCancellation), "arg");
        Assert.False(rpcTask.IsCompleted);
        await this.server.ServerMethodReached.WaitAsync(TestContext.Current.CancellationToken);
        this.clientStream.Dispose();
        await Task.Delay(ExpectedTimeout, TestContext.Current.CancellationToken);
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
    public async Task Completion_BeforeListeningAndAfterDisposal()
    {
        var rpc = new JsonRpc(Stream.Null, Stream.Null);
        Task completion = rpc.Completion;
        rpc.Dispose();
        await completion.WithCancellation(this.TimeoutToken);
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
    public async Task DispatchCompletion_RepeatedlyRefreshesDuringConnection()
    {
        for (int i = 0; i < 3; i++)
        {
            Assert.True(this.serverRpc.DispatchCompletion.IsCompleted);
            Task request = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new object?[] { "hi" }, this.TimeoutToken);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            Assert.False(this.serverRpc.DispatchCompletion.IsCompleted);
            this.server.AllowServerMethodToReturn.Set();
            await request;
            Assert.True(this.serverRpc.DispatchCompletion.IsCompleted);
        }

        this.clientRpc.Dispose();
        await this.serverRpc.Completion;
        Assert.True(this.serverRpc.DispatchCompletion.IsCompleted);
    }

    [Fact]
    public async Task DispatchCompletion_DispatchesLingerAfterDisconnect()
    {
        Task request = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new object?[] { "hi" }, this.TimeoutToken);
        await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
        this.clientRpc.Dispose();
        await this.serverRpc.Completion;

        Assert.False(this.serverRpc.DispatchCompletion.IsCompleted);
        this.server.AllowServerMethodToReturn.Set();
        await this.serverRpc.DispatchCompletion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task MultipleSyncMethodsExecuteConcurrentlyOnServer_WithClearedSyncContext()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.SynchronizationContext = null;

        var invocation1 = this.clientRpc.InvokeAsync(nameof(Server.SyncMethodWaitsToReturn));
        await this.server.ServerMethodReached.WaitAsync(UnexpectedTimeoutToken);
        var invocation2 = this.clientRpc.InvokeAsync(nameof(Server.SyncMethodWaitsToReturn));
        await this.server.ServerMethodReached.WaitAsync(UnexpectedTimeoutToken);
        this.server.AllowServerMethodToReturn.Set();
        this.server.AllowServerMethodToReturn.Set();
        await Task.WhenAll(invocation1, invocation2);
    }

    [Fact]
    public async Task MultipleSyncMethodsExecuteDoNotExecuteConcurrentlyOnServer_ByDefault()
    {
        var invocation1 = this.clientRpc.InvokeAsync(nameof(Server.SyncMethodWaitsToReturn));
        await this.server.ServerMethodReached.WaitAsync(UnexpectedTimeoutToken);
        var invocation2 = this.clientRpc.InvokeAsync(nameof(Server.SyncMethodWaitsToReturn));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.server.ServerMethodReached.WaitAsync(ExpectedTimeoutToken));
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

    [Theory, PairwiseData]
    public async Task ExceptionControllingErrorCode(ExceptionProcessing exceptionStrategy)
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = exceptionStrategy;
        this.serverRpc.ExceptionStrategy = exceptionStrategy;

        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.ThrowLocalRpcException)));
        Assert.Equal(2, exception.ErrorCode);

        // Even with ExceptionStrategy on, we do not serialize exceptions when LocalRpcException is thrown because the server is taking control over that.
        Assert.Null(exception.InnerException);
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

    [Fact]
    public async Task ReturnTypeThrowsOnDeserialization()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeWithCancellationAsync<TypeThrowsWhenDeserialized>(nameof(Server.GetTypeThrowsWhenDeserialized), cancellationToken: this.TimeoutToken)).WithCancellation(this.TimeoutToken);
        Assert.IsAssignableFrom(this.FormatterExceptionType, ex);
    }

    [Fact]
    public async Task MethodArgThrowsOnDeserialization()
    {
        var ex = await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.MethodWithArgThatFailsToDeserialize), new object[] { new TypeThrowsWhenDeserialized() }, this.TimeoutToken)).WithCancellation(this.TimeoutToken);
        var expectedErrorMessage = CreateExceptionToBeThrownByDeserializer().Message;
        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        var data = Assert.IsType<CommonErrorData>(ex.DeserializedErrorData);
        Assert.Contains(FlattenCommonErrorData(data), d => d.Message?.Contains(expectedErrorMessage) ?? false);
    }

    [Fact]
    public async Task CanPassExceptionFromServer_DeserializedErrorData()
    {
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.Equal((int)JsonRpcErrorCode.InvocationError, exception.ErrorCode);

        var errorData = Assert.IsType<CommonErrorData>(exception.DeserializedErrorData);
        Assert.NotNull(errorData.StackTrace);
        Assert.StrictEqual(COR_E_UNAUTHORIZEDACCESS, errorData.HResult);
    }

    [Theory, PairwiseData]
    public async Task ExceptionTreeThrownFromServerIsDeserializedAtClient(ExceptionProcessing exceptionStrategy)
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = exceptionStrategy;
        this.serverRpc.ExceptionStrategy = exceptionStrategy;
        this.clientRpc.LoadableTypes.Add(typeof(FileNotFoundException));
        this.clientRpc.LoadableTypes.Add(typeof(ApplicationException));

        var exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsDeeplyNestedExceptions)));

        // Verify the CommonErrorData is always available.
        {
            var outer = Assert.IsType<CommonErrorData>(exception.DeserializedErrorData);
            Assert.Equal(typeof(FileNotFoundException).FullName, outer.TypeName);
            Assert.Equal("3", outer.Message);
            Assert.NotNull(outer.StackTrace);
            var middle = Assert.IsType<CommonErrorData>(outer.Inner);
            Assert.Equal(typeof(ApplicationException).FullName, middle.TypeName);
            Assert.Equal("2", middle.Message);
            Assert.NotNull(middle.StackTrace);
            var inner = Assert.IsType<CommonErrorData>(middle.Inner);
            Assert.Equal(typeof(InvalidOperationException).FullName, inner.TypeName);
            Assert.Equal("1", inner.Message);
            Assert.NotNull(inner.StackTrace);
        }

        if (exceptionStrategy == ExceptionProcessing.ISerializable)
        {
            var outer = Assert.IsType<FileNotFoundException>(exception.InnerException);
            Assert.Equal("3", outer.Message);
            Assert.NotNull(outer.StackTrace);
            var middle = Assert.IsType<ApplicationException>(outer.InnerException);
            Assert.Equal("2", middle.Message);
            Assert.NotNull(middle.StackTrace);
            var inner = Assert.IsType<InvalidOperationException>(middle.InnerException);
            Assert.Equal("1", inner.Message);
            Assert.NotNull(inner.StackTrace);
        }
    }

    [Fact]
    public async Task DisposeOnDisconnect_NotDisposable()
    {
        var streams = FullDuplexStream.CreatePair();
        var server = new Server();
        var serverRpc = new JsonRpc(streams.Item2);
        serverRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { DisposeOnDisconnect = true });
        serverRpc.StartListening();

        streams.Item1.Dispose();
        await serverRpc.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task DisposeOnDisconnect_ServerDisposableButFeatureNotOn()
    {
        var streams = FullDuplexStream.CreatePair();
        var server = new DisposableServer();
        var serverRpc = new JsonRpc(streams.Item2);
        serverRpc.AddLocalRpcTarget(server);
        serverRpc.StartListening();

        streams.Item1.Dispose();
        await serverRpc.Completion.WithCancellation(this.TimeoutToken);
        Assert.False(server.IsDisposed);
    }

    [Theory]
    [CombinatorialData]
    public async Task DisposeOnDisconnect_SystemDisposable(bool throwFromDispose)
    {
        var streams = FullDuplexStream.CreatePair();
        var server = new DisposableServer();
        if (throwFromDispose)
        {
            server.ExceptionToThrowFromDisposal = new InvalidOperationException();
        }

        var serverRpc = new JsonRpc(streams.Item2);
        serverRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { DisposeOnDisconnect = true });
        serverRpc.StartListening();

        Assert.False(server.IsDisposed);
        streams.Item1.Dispose();
        if (throwFromDispose)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => serverRpc.Completion).WithCancellation(this.TimeoutToken);
            Assert.Same(server.ExceptionToThrowFromDisposal, ex);
        }
        else
        {
            await serverRpc.Completion.WithCancellation(this.TimeoutToken);
        }

        Assert.True(server.IsDisposed);
    }

    [Theory]
    [CombinatorialData]
    public async Task DisposeOnDisconnect_SystemAsyncDisposable(bool throwFromDispose)
    {
        var streams = FullDuplexStream.CreatePair();
        var server = new SystemAsyncDisposableServer();
        if (throwFromDispose)
        {
            server.ExceptionToThrowFromDisposal = new InvalidOperationException();
        }

        var serverRpc = new JsonRpc(streams.Item2);
        serverRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { DisposeOnDisconnect = true });
        serverRpc.StartListening();

        Assert.False(server.IsDisposed);
        streams.Item1.Dispose();
        if (throwFromDispose)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => serverRpc.Completion).WithCancellation(this.TimeoutToken);
            Assert.Same(server.ExceptionToThrowFromDisposal, ex);
        }
        else
        {
            await serverRpc.Completion.WithCancellation(this.TimeoutToken);
        }

        Assert.True(server.IsDisposed);
    }

    [Theory]
    [CombinatorialData]
    public async Task DisposeOnDisconnect_VsThreadingAsyncDisposable(bool throwFromDispose)
    {
        var streams = FullDuplexStream.CreatePair();
        var server = new VsThreadingAsyncDisposableServer();
        if (throwFromDispose)
        {
            server.ExceptionToThrowFromDisposal = new InvalidOperationException();
        }

        var serverRpc = new JsonRpc(streams.Item2);
        serverRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { DisposeOnDisconnect = true });
        serverRpc.StartListening();

        Assert.False(server.IsDisposed);
        streams.Item1.Dispose();
        if (throwFromDispose)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => serverRpc.Completion).WithCancellation(this.TimeoutToken);
            Assert.Same(server.ExceptionToThrowFromDisposal, ex);
        }
        else
        {
            await serverRpc.Completion.WithCancellation(this.TimeoutToken);
        }

        Assert.True(server.IsDisposed);
    }

    [Fact]
    public async Task SerializableExceptions()
    {
        Assert.SkipWhen(this is JsonRpcPolyTypeJsonHeadersTests, "Not (yet) supported.");

        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.LoadableTypes.Add(typeof(FileNotFoundException));

        // Create a full exception with inner exceptions. We have to throw so that its stacktrace is initialized.
        Exception? exceptionToSend;
        try
        {
            try
            {
                InvalidOperationException ex = new("IOE test exception");

                // Our more strongly typed and safer serializer does not serialize the Data dictionary.
                if (this is not JsonRpcNerdbankMessagePackLengthTests)
                {
                    ex.Data["someKey"] = "someValue";
                }

                throw ex;
            }
            catch (InvalidOperationException inner)
            {
                throw new FileNotFoundException("FNF test exception", inner);
            }
        }
        catch (FileNotFoundException outer)
        {
            exceptionToSend = outer;
        }

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { exceptionToSend }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Make sure the exception is its own unique (deserialized) instance, but equal by value.
        Assert.NotSame(this.server.ReceivedException, exceptionToSend);
        AssertExceptionEquality(exceptionToSend, this.server.ReceivedException);
    }

    [Fact]
    public void ExceptionOptions()
    {
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.ExceptionOptions = ExceptionSettings.UntrustedData);
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionOptions = this.clientRpc.ExceptionOptions with { RecursionLimit = this.clientRpc.ExceptionOptions.RecursionLimit };
    }

    [Fact]
    public async Task ExceptionRecursionLimit_ArgumentSerialization()
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionOptions = this.clientRpc.ExceptionOptions with { RecursionLimit = 2 };

        // Create a russian doll of exceptions that are one level deeper than the client should allow.
        Exception? outerException = CreateRecursiveException(this.clientRpc.ExceptionOptions.RecursionLimit + 1);

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { outerException }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Verify that the inner exceptions were truncated beyond a point.
        Assert.Equal(this.clientRpc.ExceptionOptions.RecursionLimit, CountRecursionLevel(this.server.ReceivedException));
    }

    [Fact]
    public async Task ExceptionRecursionLimit_ArgumentDeserialization()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ExceptionOptions = this.serverRpc.ExceptionOptions with { RecursionLimit = 2 };

        // Create a russian doll of exceptions that are one level deeper than the server should allow.
        Exception? outerException = CreateRecursiveException(this.serverRpc.ExceptionOptions.RecursionLimit + 1);

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { outerException }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Verify that the inner exceptions were truncated beyond a point.
        Assert.Equal(this.serverRpc.ExceptionOptions.RecursionLimit, CountRecursionLevel(this.server.ReceivedException));
    }

    [Fact]
    public async Task ExceptionRecursionLimit_ThrownSerialization()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.clientRpc.ExceptionOptions = this.clientRpc.ExceptionOptions with { RecursionLimit = 2 };

        RemoteInvocationException ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ThrowException), new object?[] { this.clientRpc.ExceptionOptions.RecursionLimit + 1 }, this.TimeoutToken));

        // Verify that the inner exceptions were truncated beyond a point.
        int actualRecursionLevel = CountRecursionLevel(ex.InnerException);
        Assert.Equal(this.clientRpc.ExceptionOptions.RecursionLimit, actualRecursionLevel);
    }

    [Fact]
    public async Task ExceptionRecursionLimit_ThrownDeserialization()
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.serverRpc.ExceptionOptions = this.serverRpc.ExceptionOptions with { RecursionLimit = 2 };

        RemoteInvocationException ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.ThrowException), new object?[] { this.serverRpc.ExceptionOptions.RecursionLimit + 1 }, this.TimeoutToken));

        // Verify that the inner exceptions were truncated beyond a point.
        int actualRecursionLevel = CountRecursionLevel(ex.InnerException);
        Assert.Equal(this.serverRpc.ExceptionOptions.RecursionLimit, actualRecursionLevel);
    }

    [Fact]
    public async Task LoadableTypesCollectionIsSettable()
    {
        LoadableTypeCollection loadableTypes = default;
        loadableTypes.Add(typeof(TaskCanceledException));
        loadableTypes.Add(typeof(OperationCanceledException));

        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ExceptionOptions = new ExceptionFilter(ExceptionSettings.UntrustedData.RecursionLimit);
        this.serverRpc.LoadableTypes = loadableTypes;

        Exception originalException = new TaskCanceledException();
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { originalException }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Verify that the server received only the base type of the exception we sent.
        Assert.IsType<OperationCanceledException>(this.server.ReceivedException);
    }

    [Fact]
    public async Task ExceptionCanDeserializeExtensibility()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ExceptionOptions = new ExceptionFilter(ExceptionSettings.UntrustedData.RecursionLimit);
        this.serverRpc.LoadableTypes.Add(typeof(TaskCanceledException));
        this.serverRpc.LoadableTypes.Add(typeof(OperationCanceledException));

        Exception originalException = new TaskCanceledException();
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { originalException }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Verify that the server received only the base type of the exception we sent.
        Assert.IsType<OperationCanceledException>(this.server.ReceivedException);
    }

    [Fact]
    public async Task ArgumentOutOfRangeException_WithNullArgValue()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.LoadableTypes.Add(typeof(ArgumentOutOfRangeException));

        Exception? exceptionToSend = new ArgumentOutOfRangeException("t", "msg");

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { exceptionToSend }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Make sure the exception is its own unique (deserialized) instance, but equal by value.
        Assert.NotSame(this.server.ReceivedException, exceptionToSend);
        Assert.Null(((ArgumentOutOfRangeException)this.server.ReceivedException!).ActualValue);
        AssertExceptionEquality(exceptionToSend, this.server.ReceivedException);
    }

    [Fact]
    public async Task ArgumentOutOfRangeException_WithStringArgValue()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.LoadableTypes.Add(typeof(ArgumentOutOfRangeException));

        Exception? exceptionToSend = new ArgumentOutOfRangeException("t", "argValue", "msg");

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { exceptionToSend }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Make sure the exception is its own unique (deserialized) instance, but equal by value.
        Assert.NotSame(this.server.ReceivedException, exceptionToSend);

        if (this.clientMessageFormatter is MessagePackFormatter or NerdbankMessagePackFormatter)
        {
            // MessagePack cannot (safely) deserialize a typeless value like ArgumentOutOfRangeException.ActualValue,
            // So assert that a placeholder was put there instead.
            Assert.Equal(exceptionToSend.Message.Replace("argValue", "<raw msgpack>"), this.server.ReceivedException!.Message);
        }
        else
        {
            AssertExceptionEquality(exceptionToSend, this.server.ReceivedException);
        }
    }

    [Fact]
    public async Task SerializableExceptions_NonExistant()
    {
        // Synthesize an exception message that refers to an exception type that does not exist.
        var exceptionToSend = new LyingException("lying message");
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { exceptionToSend }, new[] { typeof(Exception) }, this.TimeoutToken);

        // Make sure the exception is its own unique (deserialized) instance, but equal by value.
        Assert.NotSame(this.server.ReceivedException, exceptionToSend);
        AssertExceptionEquality(exceptionToSend, this.server.ReceivedException, compareType: false);

        // Verify that the base exception type was created.
        Assert.IsType<Exception>(this.server.ReceivedException);
    }

    [Fact]
    public async Task SerializableExceptions_Null()
    {
        // Set this to a non-null value so when we assert null we know the RPC server method was in fact invoked.
        this.server.ReceivedException = new InvalidOperationException();

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new object?[] { null }, new[] { typeof(Exception) }, this.TimeoutToken);
        Assert.Null(this.server.ReceivedException);
    }

    [Fact]
    public async Task NonSerializableExceptionInArgumentThrowsLocally()
    {
        // Synthesize an exception message that refers to an exception type that does not exist.
        var exceptionToSend = new NonSerializableException(Server.ExceptionMessage);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new[] { exceptionToSend }, new[] { typeof(Exception) }, this.TimeoutToken));
        Assert.IsAssignableFrom(this.FormatterExceptionType, exception);
    }

    [Fact]
    public async Task SerializableExceptions_RedirectType()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers(false);

        this.serverRpc = new JsonRpcThatSubstitutesType(this.serverMessageHandler);
        this.clientRpc = new JsonRpcThatSubstitutesType(this.clientMessageHandler);

        this.serverRpc.AddLocalRpcTarget(this.server);

        this.AddTracing();

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new object[] { new ArgumentOutOfRangeException() }, this.TimeoutToken);

        // assert that a different type was received than was transmitted.
        Assert.IsType<ArgumentException>(this.server.ReceivedException);
    }

    [Fact]
    public async Task DeserializeExceptionsWithUntypedData()
    {
        Exception exception = new Exception
        {
            Data =
            {
                { "harmlessCustomType", new CustomISerializableData(1) },
            },
        };
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.SendException), new object[] { exception }, this.TimeoutToken);

        // Assert that the data was received, but not typed as the sender intended unless type handling was turned on.
        // This is important because the received data should not determine which types are deserialized
        // for security reasons.
        object? objectOfCustomType = this.server.ReceivedException!.Data["harmlessCustomType"];

        if (this.serverMessageFormatter is JsonMessageFormatter { JsonSerializer: { TypeNameHandling: JsonNET.TypeNameHandling.Objects } })
        {
            Assert.IsType<CustomISerializableData>(objectOfCustomType);
        }
        else
        {
            Assert.IsNotType<CustomISerializableData>(objectOfCustomType);
        }

        this.Logger.WriteLine("objectOfCustomType type: {0}", objectOfCustomType?.GetType().FullName!);
    }

    [Fact]
    public void CancellationStrategy_ConfigurationLocked()
    {
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.CancellationStrategy = null);
        Assert.NotNull(this.clientRpc.CancellationStrategy);
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.CancellationStrategy = null;
    }

    [Fact]
    public void ActivityTracingStrategy_ConfigurationLocked()
    {
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.ActivityTracingStrategy = null);
        Assert.Null(this.clientRpc.ActivityTracingStrategy);
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ActivityTracingStrategy = null;
    }

    [Fact]
    public async Task ActivityStartsOnServerIfNoParent_CorrelationManager()
    {
        var strategyListener = new CollectingTraceListener();
        var strategy = new CorrelationManagerTracingStrategy
        {
            TraceSource = new TraceSource("strategy", SourceLevels.ActivityTracing | SourceLevels.Information)
            {
                Listeners = { strategyListener },
            },
        };
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ActivityTracingStrategy = strategy;
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ActivityTracingStrategy = strategy;

        Assert.Equal(Guid.Empty, Trace.CorrelationManager.ActivityId);
        Guid serverActivityId = await this.clientRpc.InvokeWithCancellationAsync<Guid>(nameof(Server.GetCorrelationManagerActivityId), cancellationToken: this.TimeoutToken);
        Assert.Equal(Guid.Empty, Trace.CorrelationManager.ActivityId);

        Assert.NotEqual(Guid.Empty, serverActivityId);
    }

    [Fact]
    public async Task ActivityStartsOnServerIfNoParent_Activity()
    {
        var strategy = new ActivityTracingStrategy();
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ActivityTracingStrategy = strategy;
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ActivityTracingStrategy = strategy;

        Assert.Null(Activity.Current);
        string? parentActivityId = await this.clientRpc.InvokeWithCancellationAsync<string?>(nameof(Server.GetParentActivityId), cancellationToken: this.TimeoutToken);
        string? activityId = await this.clientRpc.InvokeWithCancellationAsync<string?>(nameof(Server.GetActivityId), cancellationToken: this.TimeoutToken);
        Assert.Null(Activity.Current);

        Assert.Null(parentActivityId);
        Assert.NotNull(activityId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k=v")]
    [InlineData("k=v,k2=v2")]
    public async Task CorrelationManagerActivitiesPropagate(string? traceState)
    {
        var strategyListener = new CollectingTraceListener();
        var strategy = new CorrelationManagerTracingStrategy
        {
            TraceSource = new TraceSource("strategy", SourceLevels.ActivityTracing | SourceLevels.Information)
            {
                Listeners = { strategyListener },
            },
        };
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ActivityTracingStrategy = strategy;
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ActivityTracingStrategy = strategy;

        Guid clientActivityId = Guid.NewGuid();
        Trace.CorrelationManager.ActivityId = clientActivityId;
        CorrelationManagerTracingStrategy.TraceState = traceState;
        Guid serverActivityId = await this.clientRpc.InvokeWithCancellationAsync<Guid>(nameof(Server.GetCorrelationManagerActivityId), cancellationToken: this.TimeoutToken);
        string? serverTraceState = await this.clientRpc.InvokeWithCancellationAsync<string?>(nameof(Server.GetTraceState), new object[] { true }, cancellationToken: this.TimeoutToken);

        // The activity ID on the server should not *equal* the client's or else it doesn't look like a separate chain in Service Trace Viewer.
        Assert.NotEqual(clientActivityId, serverActivityId);

        // The activity ID should also not be empty.
        Assert.NotEqual(Guid.Empty, serverActivityId);

        // But whatever the activity ID is randomly assigned to be, a Transfer should have been recorded.
        Assert.Contains(strategyListener.Transfers, t => t.CurrentActivityId == clientActivityId && t.RelatedActivityId == serverActivityId);

        // When traceState was empty, the server is allowed to see null.
        if (traceState == string.Empty)
        {
            Assert.True(string.IsNullOrEmpty(serverTraceState));
        }
        else
        {
            Assert.Equal(traceState, serverTraceState);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k=v")]
    [InlineData("k=v,k2=v2")]
    public async Task ActivityIdActivitiesPropagate(string? traceState)
    {
        var strategy = new ActivityTracingStrategy();
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ActivityTracingStrategy = strategy;
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.ActivityTracingStrategy = strategy;

        var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        try
        {
            activity.TraceStateString = traceState;
            string? serverParentActivityId = await this.clientRpc.InvokeWithCancellationAsync<string?>(nameof(Server.GetParentActivityId), cancellationToken: this.TimeoutToken);
            Assert.Equal(activity.Id, serverParentActivityId);
            string? serverTraceState = await this.clientRpc.InvokeWithCancellationAsync<string?>(nameof(Server.GetTraceState), new object[] { false }, cancellationToken: this.TimeoutToken);

            // When traceState was empty, the server is allowed to see null.
            if (traceState == string.Empty)
            {
                Assert.True(string.IsNullOrEmpty(serverTraceState));
            }
            else
            {
                Assert.Equal(traceState, serverTraceState);
            }
        }
        finally
        {
            activity.Stop();
        }
    }

    /// <summary>
    /// Sets up <see cref="JsonRpc"/> on both sides to record activity traces as XML so an engineer can manually validate
    /// that <see href="https://docs.microsoft.com/en-us/dotnet/framework/wcf/service-trace-viewer-tool-svctraceviewer-exe#using-the-service-trace-viewer-tool">Service Trace Viewer</see>
    /// can open and compose into a holistic view.
    /// </summary>
    [Fact]
    public async Task ActivityTracing_IntegrationTest()
    {
        string logDir = Path.Combine(Environment.CurrentDirectory, this.GetType().Name + "." + nameof(this.ActivityTracing_IntegrationTest));
        this.Logger.WriteLine("Log files dropped to \"{0}\".", logDir);
        Directory.CreateDirectory(logDir);

        // We are intentionally setting up many isolated listeners, TraceSources and strategies to simulate this all being done across processes where they couldn't be shared.
        using var clientListener = new XmlWriterTraceListener(Path.Combine(logDir, "client.svclog"));
        using var clientRpcListener = new XmlWriterTraceListener(Path.Combine(logDir, "client-rpc.svclog"));
        using var clientStrategyListener = new XmlWriterTraceListener(Path.Combine(logDir, "client-strategy.svclog"));
        using var serverRpcListener = new XmlWriterTraceListener(Path.Combine(logDir, "server-rpc.svclog"));
        using var serverStrategyListener = new XmlWriterTraceListener(Path.Combine(logDir, "server-strategy.svclog"));
        using var serverTargetListener = new XmlWriterTraceListener(Path.Combine(logDir, "server.svclog"));

        this.server.TraceSource = new TraceSource("server-target", SourceLevels.ActivityTracing | SourceLevels.Information)
        {
            Listeners = { serverTargetListener },
        };

        var clientTraceSource = new TraceSource("client", SourceLevels.ActivityTracing | SourceLevels.Information)
        {
            Listeners = { clientListener },
        };

        ConfigureTracing(this.clientRpc, "client", clientRpcListener, clientStrategyListener);
        ConfigureTracing(this.serverRpc, "server", serverRpcListener, serverStrategyListener);

        Trace.CorrelationManager.ActivityId = Guid.NewGuid();
        clientTraceSource.TraceInformation("I feel like making an RPC call.");
        await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.TraceSomething), new object?[] { "hi" }, this.TimeoutToken);
        clientTraceSource.TraceInformation("Call completed.");

        // Remove the listeners before we dispose them.
        this.serverRpc.TraceSource.Listeners.Remove(serverRpcListener);
        this.clientRpc.TraceSource.Listeners.Remove(clientRpcListener);

        static void ConfigureTracing(JsonRpc jsonRpc, string name, XmlWriterTraceListener listener, XmlWriterTraceListener strategyListener)
        {
            jsonRpc.AllowModificationWhileListening = true;
            jsonRpc.TraceSource.Switch.Level |= SourceLevels.ActivityTracing | SourceLevels.Information;
            jsonRpc.TraceSource.Listeners.Add(listener);
            jsonRpc.ActivityTracingStrategy = new CorrelationManagerTracingStrategy
            {
                TraceSource = new TraceSource($"{name}-strategy", SourceLevels.ActivityTracing | SourceLevels.Information)
                {
                    Listeners = { strategyListener },
                },
            };
        }
    }

    /// <summary>
    /// Verifies that an activity representing an incoming RPC request lasts the full duration of the async server method
    /// rather than just till its first yield.
    /// </summary>
    [Fact]
    public async Task IncomingActivityStopsAfterAsyncTargetMethodCompletes()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        TaskCompletionSource<object?> started = new();
        TaskCompletionSource<object?> stopped = new();
        this.serverRpc.ActivityTracingStrategy = new MockActivityTracingStrategy
        {
            Inbound = req =>
            {
                started.SetResult(null);
                return new DisposableAction(() => stopped.SetResult(null));
            },
        };
        Task task = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new object?[] { "arg" }, this.TimeoutToken);
        await started.Task.WithCancellation(this.TimeoutToken);
        await Assert.ThrowsAsync<TimeoutException>(() => stopped.Task.WithTimeout(ExpectedTimeout));
        this.server.AllowServerMethodToReturn.Set();
        await stopped.Task.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public void JoinableTaskFactory_ThrowsAfterRunning()
    {
        Assert.Throws<InvalidOperationException>(() => this.clientRpc.JoinableTaskFactory = null);
    }

    /// <summary>
    /// Asserts that when both client and server are JTF-aware (and in the same process),
    /// that no deadlock occurs when the client blocks the main thread that the server needs.
    /// </summary>
    [UIFact]
    public void JoinableTaskFactory_IntegrationBothSides_IntraProcess()
    {
        // Set up a main thread and JoinableTaskContext.
        JoinableTaskContext jtc = new();

        // Configure the client and server to understand JTF.
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.JoinableTaskFactory = jtc.Factory;
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.JoinableTaskFactory = jtc.Factory;

        // Tell the server to require the main thread to get something done.
        this.server.JoinableTaskFactory = jtc.Factory;

        jtc.Factory.Run(async delegate
        {
            string result = await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(this.server.AsyncMethod), new object?[] { "hi" }, this.TimeoutToken).WithCancellation(this.TimeoutToken);
            Assert.Equal("hi!", result);
        });
    }

    /// <summary>
    /// Asserts that when only the client is JTF-aware, that no deadlock occurs when the client blocks the main thread
    /// and the server calls back to the client for something that needs the main thread as part of processing the client's request
    /// via the <em>same</em> <see cref="JsonRpc"/> instance.
    /// </summary>
    [UIFact]
    public void JoinableTaskFactory_IntegrationClientSideOnly()
    {
        // Set up a main thread and JoinableTaskContext.
        JoinableTaskContext jtc = new();

        // Configure the client (only) to understand JTF.
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.JoinableTaskFactory = jtc.Factory;

        const string CallbackMethodName = "ClientNeedsMainThread";
        this.clientRpc.AddLocalRpcMethod(CallbackMethodName, new Func<Task>(async delegate
        {
            await jtc.Factory.SwitchToMainThreadAsync(this.TimeoutToken);
        }));

        this.server.Tests = this;

        jtc.Factory.Run(async delegate
        {
            await this.clientRpc.InvokeWithCancellationAsync(nameof(this.server.Callback), new object?[] { CallbackMethodName }, this.TimeoutToken).WithCancellation(this.TimeoutToken);
        });
    }

    /// <summary>
    /// Asserts that when only the client is JTF-aware, that no deadlock occurs when the client blocks the main thread
    /// and the server calls "back" to the client for something that needs the main thread as part of processing the client's request
    /// <em>using a different <see cref="JsonRpc"/> connection</em>.
    /// </summary>
    [UIFact]
    public void JoinableTaskFactory_IntegrationClientSideOnly_ManyConnections()
    {
        // Set up a main thread and JoinableTaskContext.
        JoinableTaskContext jtc = new();

        // Configure the client (only) to understand JTF.
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.JoinableTaskFactory = jtc.Factory;

        // Set up the alternate JsonRpc connection.
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.InitializeFormattersAndHandlers(
            streams.Item1,
            streams.Item2,
            out _,
            out _,
            out IJsonRpcMessageHandler alternateServerHandler,
            out IJsonRpcMessageHandler alternateClientHandler,
            controlledFlushingClient: false);
        JsonRpc alternateServerRpc = new(alternateServerHandler, this.server);
        JsonRpc alternateClientRpc = new(alternateClientHandler) { JoinableTaskFactory = jtc.Factory };
        this.server.AlternateRpc = alternateServerRpc;

        alternateServerRpc.TraceSource = new TraceSource("ALT Server", SourceLevels.Verbose | SourceLevels.ActivityTracing);
        alternateClientRpc.TraceSource = new TraceSource("ALT Client", SourceLevels.Verbose | SourceLevels.ActivityTracing);

        alternateServerRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        alternateClientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        // Arrange for a method on the simulated client that requires the UI thread, and that the server will call back to.
        bool clientCalledBackViaAlternate = false;
        const string CallbackMethodName = "ClientNeedsMainThread";
        alternateClientRpc.AddLocalRpcMethod(CallbackMethodName, new Func<Task>(async delegate
        {
            await jtc.Factory.SwitchToMainThreadAsync(this.TimeoutToken);
            clientCalledBackViaAlternate = true;
        }));

        alternateServerRpc.StartListening();
        alternateClientRpc.StartListening();

        jtc.Factory.Run(async delegate
        {
            await this.clientRpc.InvokeWithCancellationAsync(nameof(this.server.CallbackOnAnotherConnection), new object?[] { CallbackMethodName }, this.TimeoutToken).WithCancellation(this.TimeoutToken);
        });

        Assert.True(clientCalledBackViaAlternate);
    }

    /// <summary>
    /// Asserts that when <see cref="JsonRpc.JoinableTaskTracker"/> is set to a unique instance, the deadlock avoidance fails.
    /// </summary>
    [UIFact]
    public async Task JoinableTaskFactory_IntegrationClientSideOnly_ManyConnections_UniqueTrackerLeadsToDeadlock()
    {
        // Set up a main thread and JoinableTaskContext.
        JoinableTaskContext jtc = new();

        // Track our async work so our test doesn't exit before our UI thread requests do,
        // or the test process will crash.
        JoinableTaskCollection jtCollection = jtc.CreateCollection();
        JoinableTaskFactory jtf = jtc.CreateFactory(jtCollection);

        // Configure the client (only) to understand JTF.
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.JoinableTaskFactory = jtf;

        // Set up the alternate JsonRpc connection.
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.InitializeFormattersAndHandlers(
            streams.Item1,
            streams.Item2,
            out _,
            out _,
            out IJsonRpcMessageHandler alternateServerHandler,
            out IJsonRpcMessageHandler alternateClientHandler,
            controlledFlushingClient: false);
        JsonRpc alternateServerRpc = new(alternateServerHandler, this.server) { JoinableTaskTracker = new() };
        JsonRpc alternateClientRpc = new(alternateClientHandler) { JoinableTaskFactory = jtf };
        this.server.AlternateRpc = alternateServerRpc;

        alternateServerRpc.TraceSource = new TraceSource("ALT Server", SourceLevels.Verbose | SourceLevels.ActivityTracing);
        alternateClientRpc.TraceSource = new TraceSource("ALT Client", SourceLevels.Verbose | SourceLevels.ActivityTracing);

        alternateServerRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        alternateClientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        // Arrange for a method on the simulated client that requires the UI thread, and that the server will call back to.
        const string CallbackMethodName = "ClientNeedsMainThread";
        alternateClientRpc.AddLocalRpcMethod(CallbackMethodName, new Func<Task>(async delegate
        {
            await jtf.SwitchToMainThreadAsync(this.TimeoutToken);
        }));

        alternateServerRpc.StartListening();
        alternateClientRpc.StartListening();

        jtf.Run(async delegate
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(this.server.CallbackOnAnotherConnection), new object?[] { CallbackMethodName }, this.TimeoutToken).WithCancellation(ExpectedTimeoutToken));
        });

        // Drain any UI thread requests before exiting the test.
        await this.server.ServerMethodCompleted.Task.WithCancellation(this.TimeoutToken);
        await jtCollection.JoinTillEmptyAsync();
        await Task.Yield();
    }

    [Fact]
    public async Task InvokeWithParameterObject_WithRenamingAttributes()
    {
        var param = new ParamsObjectWithCustomNames { TheArgument = "hello" };
        string result = await this.clientRpc.InvokeWithParameterObjectAsync<string>(nameof(Server.ServerMethod), param, this.TimeoutToken);
        Assert.Equal(param.TheArgument + "!", result);
    }

    [Fact]
    public virtual async Task CanPassAndCallPrivateMethodsObjects()
    {
        Assert.SkipWhen(this is JsonRpcPolyTypeJsonHeadersTests, "Not (yet) supported.");

        var result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new Foo { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);

        // anonymous types are not supported when TypeHandling is set to "Object" or "Auto".
        if (!this.IsTypeNameHandlingEnabled)
        {
            result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new { Bar = "bar", Bazz = 1000 });
            Assert.NotNull(result);
            Assert.Equal("bar!", result.Bar);
            Assert.Equal(1001, result.Bazz);
        }
    }

    [Fact]
    public virtual async Task CanPassExceptionFromServer_ErrorData()
    {
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.Equal((int)JsonRpcErrorCode.InvocationError, exception.ErrorCode);

        var errorDataJToken = (JsonNET.Linq.JToken?)exception.ErrorData;
        Assert.NotNull(errorDataJToken);
        var errorData = errorDataJToken!.ToObject<CommonErrorData>(new JsonNET.JsonSerializer());
        Assert.NotNull(errorData?.StackTrace);
        Assert.StrictEqual(COR_E_UNAUTHORIZEDACCESS, errorData?.HResult);
    }

    [Fact]
    public async Task InvokeWithParameterObjectAsync_FormatterIntrinsic()
    {
        string arg = "some value";
        object[] variousArgs = this.CreateFormatterIntrinsicParamsObject(arg);
        Assert.SkipWhen(variousArgs.Length == 0, "This test is only meaningful when the formatter supports intrinsic types.");

        foreach (object args in variousArgs)
        {
            var invokeTask = this.clientRpc.InvokeWithParameterObjectAsync<string>(nameof(Server.AsyncMethod), args, this.TimeoutToken);
            this.server.AllowServerMethodToReturn.Set();
            string result = await invokeTask;
            Assert.Equal($"{arg}!", result);
        }
    }

    protected static Exception CreateExceptionToBeThrownByDeserializer() => new Exception("This exception is meant to be thrown.");

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

        this.clientTraces.Dispose();
        this.serverTraces.Dispose();
        base.Dispose(disposing);
    }

    protected void InitializeFormattersAndHandlers(bool controlledFlushingClient = false) => this.InitializeFormattersAndHandlers(
        this.serverStream,
        this.clientStream,
        out this.serverMessageFormatter,
        out this.clientMessageFormatter,
        out this.serverMessageHandler,
        out this.clientMessageHandler,
        controlledFlushingClient);

    protected abstract void InitializeFormattersAndHandlers(
        Stream serverStream,
        Stream clientStream,
        out IJsonRpcMessageFormatter serverMessageFormatter,
        out IJsonRpcMessageFormatter clientMessageFormatter,
        out IJsonRpcMessageHandler serverMessageHandler,
        out IJsonRpcMessageHandler clientMessageHandler,
        bool controlledFlushingClient);

    /// <summary>
    /// Creates objects that each may be passed to <see cref="Server.AsyncMethod"/> as the whole parameter object.
    /// The object should be an intrinsic type for the formatter that requires no special serialization.
    /// </summary>
    protected abstract object[] CreateFormatterIntrinsicParamsObject(string arg);

    protected override Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated = -1, int iterations = 100, int allowedAttempts = 10)
    {
        // Make sure we aren't logging anything but errors.
        this.serverRpc.TraceSource.Switch.Level = SourceLevels.Error;
        this.clientRpc.TraceSource.Switch.Level = SourceLevels.Error;

        return base.CheckGCPressureAsync(scenario, maxBytesAllocated, iterations, allowedAttempts);
    }

    protected void ReinitializeRpcWithoutListening(bool controlledFlushingClient = false)
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers(controlledFlushingClient);

        this.serverRpc = new JsonRpc(this.serverMessageHandler, this.server);
        this.clientRpc = new JsonRpc(this.clientMessageHandler);

        this.AddTracing();
    }

    protected void AddTracing()
    {
        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose | SourceLevels.ActivityTracing);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose | SourceLevels.ActivityTracing);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.serverRpc.TraceSource.Listeners.Add(this.serverTraces = new CollectingTraceListener());
        this.clientRpc.TraceSource.Listeners.Add(this.clientTraces = new CollectingTraceListener());
    }

    private static void AssertExceptionEquality(Exception? expected, Exception? actual, bool compareType = true)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null || actual is null)
        {
            return;
        }

        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.Data, actual.Data);
        Assert.Equal(expected.HResult, actual.HResult);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.HelpLink, actual.HelpLink);
        Assert.Equal(expected.StackTrace, actual.StackTrace);

        if (compareType)
        {
            Assert.Equal(expected.GetType(), actual.GetType());
        }

        AssertExceptionEquality(expected.InnerException, actual.InnerException);
    }

    private static void SendObject(Stream receivingStream, object jsonObject)
    {
        Requires.NotNull(receivingStream, nameof(receivingStream));
        Requires.NotNull(jsonObject, nameof(jsonObject));

        string json = JsonNET.JsonConvert.SerializeObject(jsonObject);
        string header = $"Content-Length: {json.Length}\r\n\r\n";
        byte[] buffer = Encoding.ASCII.GetBytes(header + json);
        receivingStream.Write(buffer, 0, buffer.Length);
    }

    private static IEnumerable<CommonErrorData> FlattenCommonErrorData(CommonErrorData? errorData)
    {
        while (errorData is object)
        {
            yield return errorData;
            errorData = errorData.Inner;
        }
    }

    private static Exception CreateRecursiveException(int recursionCount)
    {
        Requires.Range(recursionCount > 0, nameof(recursionCount));
        Exception? outerException = null;
        for (int i = recursionCount; i >= 1; i--)
        {
            string message = $"Exception #{i}";

            // We mix up the exception types to verify that any recursion counting in the library isn't dependent on the exception type being consistent.
            outerException = i % 2 == 0 ? new Exception(message, outerException) : new InvalidOperationException(message, outerException);
        }

        return outerException!;
    }

    private static int CountRecursionLevel(Exception? ex)
    {
        int recursionLevel = 0;
        while (ex is object)
        {
            recursionLevel++;
            ex = ex.InnerException;
        }

        return recursionLevel;
    }

    private static int CountRecursionLevel(CommonErrorData? error)
    {
        int recursionLevel = 0;
        while (error is object)
        {
            recursionLevel++;
            error = error.Inner;
        }

        return recursionLevel;
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

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyAsync(nameof(Server.MethodWithProgressParameter), new XAndYPropertiesWithProgress { p = progress }));
        Assert.IsType<NotSupportedException>(ex.GetBaseException());

        await progress.WaitAsync();

        return weakRef;
    }

    private async Task InvokeMethodWithProgressParameter(IProgress<int> progress)
    {
        await this.clientRpc.InvokeWithParameterObjectAsync<int>(nameof(Server.MethodWithProgressParameter), NamedArgs.Create(new { p = progress }), this.TimeoutToken);
    }

    public class BaseClass
    {
        protected readonly TaskCompletionSource<string> notificationTcs = new TaskCompletionSource<string>();

        public string BaseMethod() => "base";

        public virtual string VirtualBaseMethod() => "base";

        public string RedeclaredBaseMethod() => "base";
    }

#pragma warning disable CA1801 // use all parameters
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial class Server : BaseClass, IServerDerived
    {
        internal const string ExceptionMessage = "some message";
        internal const string ThrowAfterCancellationMessage = "Throw after cancellation";

        public bool NullPassed { get; private set; }

        public AsyncAutoResetEvent AllowServerMethodToReturn { get; } = new AsyncAutoResetEvent();

        public AsyncAutoResetEvent ServerMethodReached { get; } = new AsyncAutoResetEvent();

        public TaskCompletionSource<object?> ServerMethodCompleted { get; } = new TaskCompletionSource<object?>();

        public Task<string> NotificationReceived => this.notificationTcs.Task;

        public bool DelayAsyncMethodWithCancellation { get; set; }

        internal Exception? ReceivedException { get; set; }

        internal JsonRpcTests? Tests { get; set; }

        internal TraceSource? TraceSource { get; set; }

        internal JsonRpc? AlternateRpc { get; set; }

        internal JoinableTaskFactory? JoinableTaskFactory { get; set; }

        public static string ServerMethod(string argument)
        {
            return argument + "!";
        }

        public static string TestParameter(JsonNET.Linq.JToken token)
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

        public static string TestInvalidMethod(JsonNET.Linq.JToken test)
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
        public static int MethodWithSingleObjectParameter(XAndYProperties fields)
        {
            return fields.x + fields.y;
        }

        public static int MethodWithSingleObjectParameterWithoutDeserializationProperty(XAndYProperties fields)
        {
            return fields.x + fields.y;
        }

        [JsonRpcMethod("test/MethodWithSingleObjectParameterVAndW", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterVAndW(VAndWProperties fields)
        {
            return fields.v + fields.w;
        }

        [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterAndCancellationToken(XAndYProperties fields, CancellationToken token)
        {
            return fields.x + fields.y;
        }

        [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterWithDefaultValue(XAndYProperties? arg = null)
        {
            return arg is not null ? arg.x + arg.y : -1;
        }

        [JsonRpcMethod("test/MethodWithSingleObjectParameterWithProgress", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithSingleObjectParameterWithProgress(XAndYPropertiesWithProgress fields)
        {
            fields.p?.Report(fields.x + fields.y);
            return fields.x + fields.y;
        }

        [JsonRpcMethod("test/MethodWithObjectAndExtraParameters", UseSingleObjectParameterDeserialization = true)]
        public static int MethodWithObjectAndExtraParameters(XAndYProperties fields, int anotherParameter)
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

        public static void MethodWithUnserializableProgressType(IProgress<TypeThrowsWhenSerialized> progress)
        {
            progress.Report(new TypeThrowsWhenSerialized());
        }

        public static int MethodWithInvalidProgressParameter(Progress<int> p)
        {
            return 1;
        }

        public int MethodWithParameterContainingIProgress(XAndYPropertiesWithProgress p)
        {
            int sum = p.x + p.y;
            p.p?.Report(1);
            return sum;
        }

        public void MethodWithMultipleProgressParameters(IProgress<int> progress1, IProgress<int> progress2)
        {
            progress1.Report(1);
            progress2.Report(2);
        }

        public int InstanceMethodWithSingleObjectParameterAndCancellationToken(XAndYProperties fields, CancellationToken token)
        {
            return fields.x + fields.y;
        }

        public int InstanceMethodWithSingleObjectParameterButNoAttribute(XAndYProperties fields)
        {
            return fields.x + fields.y;
        }

        public TypeThrowsWhenDeserialized GetTypeThrowsWhenDeserialized() => new TypeThrowsWhenDeserialized();

        public void MethodWithArgThatFailsToDeserialize(TypeThrowsWhenDeserialized arg1)
        {
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

        public void MethodThatThrowsDeeplyNestedExceptions()
        {
            try
            {
                try
                {
                    throw new InvalidOperationException("1");
                }
                catch (InvalidOperationException ex)
                {
                    throw new ApplicationException("2", ex);
                }
            }
            catch (ApplicationException ex)
            {
                throw new FileNotFoundException("3", ex);
            }
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
            this.NullPassed = value is null;
            return null;
        }

        public void NotificationMethod(string arg)
        {
            this.notificationTcs.SetResult(arg);
        }

        public CustomSerializedType RepeatSpecialType(CustomSerializedType value)
        {
            return new CustomSerializedType { Value = value.Value + "!" };
        }

        public void RepeatSpecialType_ViaProgress(string value, IProgress<CustomSerializedType> progress)
        {
            progress?.Report(new CustomSerializedType { Value = value + "!" });
        }

        public string ExpectEncodedA(string arg)
        {
            Assert.Equal("YQ==", arg);
            return arg;
        }

        public string? RepeatString(string? arg) => arg;

        public void TraceSomething(string message) => this.TraceSource?.TraceInformation(message);

        public async Task<string> AsyncMethod(string arg)
        {
            if (this.JoinableTaskFactory is not null)
            {
                await this.JoinableTaskFactory.SwitchToMainThreadAsync();
            }

            await Task.Yield();
            return arg + "!";
        }

        public async Task<int> AsyncMethodWithCancellationAndNoArgs(CancellationToken cancellationToken)
        {
            await Task.Yield();
            return 5;
        }

        public void SyncMethodWithCancellation(bool waitForCancellation, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();
            if (waitForCancellation)
            {
                var mres = new ManualResetEventSlim();
                cancellationToken.Register(() => mres.Set());
                mres.Wait(CancellationToken.None);
            }

            cancellationToken.ThrowIfCancellationRequested();
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

        public async Task Callback(string clientCallbackMethod, CancellationToken cancellationToken)
        {
            try
            {
                Verify.Operation(this.Tests is object, $"Set the {nameof(this.Tests)} property first.");
                await this.Tests.serverRpc.InvokeWithCancellationAsync(clientCallbackMethod, cancellationToken: cancellationToken);
                this.ServerMethodCompleted.SetResult(null);
            }
            catch (Exception ex)
            {
                this.ServerMethodCompleted.SetException(ex);
                throw;
            }
        }

        public async Task CallbackOnAnotherConnection(string clientCallbackMethod, CancellationToken cancellationToken)
        {
            try
            {
                Verify.Operation(this.AlternateRpc is object, $"Set the {nameof(this.AlternateRpc)} field first.");
                await this.AlternateRpc.InvokeWithCancellationAsync(clientCallbackMethod, cancellationToken: cancellationToken);
                this.ServerMethodCompleted.SetResult(null);
            }
            catch (Exception ex)
            {
                this.ServerMethodCompleted.SetException(ex);
                throw;
            }
        }

        public async Task<string> AsyncMethodWithCancellation(string arg, CancellationToken cancellationToken)
        {
            try
            {
                this.ServerMethodReached.Set();

                // TODO: remove when https://github.com/Microsoft/vs-threading/issues/185 is fixed
                if (this.DelayAsyncMethodWithCancellation)
                {
                    await Task.Delay(UnexpectedTimeout, cancellationToken);
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
            await this.AllowServerMethodToReturn.WaitAsync(CancellationToken.None);
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

        public async Task<string> AsyncMethodWithJTokenAndCancellation(JsonNET.Linq.JToken paramObject, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();

            // TODO: remove when https://github.com/Microsoft/vs-threading/issues/185 is fixed
            if (this.DelayAsyncMethodWithCancellation)
            {
                await Task.Delay(UnexpectedTimeout, cancellationToken);
            }

            await this.AllowServerMethodToReturn.WaitAsync(cancellationToken);
            return paramObject.ToString(JsonNET.Formatting.None) + "!";
        }

        public async Task<string> AsyncMethodFaultsAfterCancellation(string arg, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();
            await this.AllowServerMethodToReturn.WaitAsync(CancellationToken.None);
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
            throw new Exception(ExceptionMessage);
        }

        public async Task AsyncMethodThatThrowsNonSerializableException()
        {
            await Task.Yield();
            throw new NonSerializableException(ExceptionMessage);
        }

        public async Task AsyncMethodThatThrowsExceptionThatThrowsOnSerialization()
        {
            await Task.Yield();
            throw new ThrowOnSerializeException(ExceptionMessage);
        }

        public async Task AsyncMethodThatThrowsAnExceptionWithoutDeserializingConstructor()
        {
            await Task.Yield();
            throw new ExceptionMissingDeserializingConstructor(ExceptionMessage);
        }

        public void ThrowPrivateSerializableException() => throw new PrivateSerializableException();

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

        [JsonRpcMethod]
        public Task<int> MethodWithAttributeThatEndsInAsync()
        {
            return Task.FromResult(6);
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

        public TypeThrowsWhenSerialized GetUnserializableType() => new TypeThrowsWhenSerialized();

        public void ThrowLocalRpcException()
        {
            throw new LocalRpcException { ErrorCode = 2, ErrorData = new CustomErrorData("hi") };
        }

        public void SendException(Exception? ex)
        {
            this.ReceivedException = ex;
        }

        public void ThrowException(int recursionCount) => throw CreateRecursiveException(recursionCount);

        public Guid GetCorrelationManagerActivityId() => Trace.CorrelationManager.ActivityId;

        public string? GetParentActivityId() => Activity.Current?.ParentId;

        public string? GetActivityId() => Activity.Current?.Id;

        public string? GetTraceState(bool useCorrelationManager) => useCorrelationManager ? CorrelationManagerTracingStrategy.TraceState : Activity.Current?.TraceStateString;

        public bool MethodOnDerived() => true;

        [JsonRpcIgnore]
        public void PublicIgnoredMethod()
        {
        }

        public void InterfaceIgnoredMethod()
        {
        }

        int IServer.Add_ExplicitInterfaceImplementation(int a, int b) => a + b;

        internal void InternalMethod()
        {
            this.ServerMethodReached.Set();
        }

        [JsonRpcMethod]
        internal void InternalMethodWithAttribute()
        {
            this.ServerMethodReached.Set();
        }

        [JsonRpcIgnore]
        internal void InternalIgnoredMethod()
        {
        }

        [GenerateShape, MessagePack.MessagePackObject(keyAsPropertyName: true)]
        internal partial record CustomErrorData(string MyCustomData);
    }
#pragma warning restore CA1801 // use all parameters

    public class AdditionalServerTargetOne
    {
        public int PlusOne(int arg)
        {
            return arg + 1;
        }
    }

    public class DisposableServer : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        internal Exception? ExceptionToThrowFromDisposal { get; set; }

        public void Dispose()
        {
            this.IsDisposed = true;
            if (this.ExceptionToThrowFromDisposal is object)
            {
                throw this.ExceptionToThrowFromDisposal;
            }
        }
    }

    public class SystemAsyncDisposableServer : System.IAsyncDisposable
    {
        internal bool IsDisposed { get; private set; }

        internal Exception? ExceptionToThrowFromDisposal { get; set; }

        public ValueTask DisposeAsync()
        {
            this.IsDisposed = true;
            if (this.ExceptionToThrowFromDisposal is object)
            {
                throw this.ExceptionToThrowFromDisposal;
            }

            return default;
        }
    }

    [DataContract]
    [GenerateShape]
    public partial class ParamsObjectWithCustomNames
    {
        [DataMember(Name = "argument")]
        [STJ.JsonPropertyName("argument")]
        [PropertyShape(Name = "argument")]
        public string? TheArgument { get; set; }
    }

    public class VsThreadingAsyncDisposableServer : Microsoft.VisualStudio.Threading.IAsyncDisposable
    {
        internal bool IsDisposed { get; private set; }

        internal Exception? ExceptionToThrowFromDisposal { get; set; }

        public Task DisposeAsync()
        {
            this.IsDisposed = true;
            if (this.ExceptionToThrowFromDisposal is object)
            {
                throw this.ExceptionToThrowFromDisposal;
            }

            return Task.CompletedTask;
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

    public class ServerWithConflictingAttributes
    {
        [JsonRpcMethod, JsonRpcIgnore]
        public void BadMethod()
        {
        }
    }

    public class ServerWithConflictingAttributesViaInheritance : IServerWithIgnoredMethod
    {
        [JsonRpcMethod]
        public void IgnoredMethod()
        {
        }
    }

    [DataContract]
    [GenerateShape]
    public partial class Foo
    {
        [DataMember(Order = 0, IsRequired = true)]
        [STJ.JsonRequired, STJ.JsonPropertyOrder(0)]
        [PropertyShape(Order = 0, IsRequired = true)]
        public string? Bar { get; set; }

        [DataMember(Order = 1)]
        [STJ.JsonPropertyOrder(1)]
        [PropertyShape(Order = 1)]
        public int Bazz { get; set; }
    }

    [GenerateShape]
    public partial class CustomSerializedType
    {
        // Ignore this so default serializers will drop it, proving that custom serializers were used if the value propagates.
        [JsonNET.JsonIgnore]
        [IgnoreDataMember]
        [PropertyShape(Ignore = true)]
        public string? Value { get; set; }
    }

    [Serializable, DataContract, GenerateShape]
    public partial class CustomISerializableData : ISerializable
    {
        [MessagePack.SerializationConstructor]
        public CustomISerializableData(int major)
        {
            this.Major = major;
        }

        protected CustomISerializableData(SerializationInfo info, StreamingContext context)
        {
            this.Major = info.GetInt32(nameof(this.Major));
        }

        [DataMember]
        public int Major { get; set; }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(this.Major), this.Major);
        }
    }

    [DataContract]
    public class TypeThrowsWhenSerialized
    {
        [DataMember]
        public string Property
        {
            get => throw new Exception("Can't touch this.");
            set => throw new NotImplementedException();
        }
    }

    [JsonConverter(typeof(JsonRpcPolyTypeJsonHeadersTests.TypeThrowsWhenDeserializedConverter))]
    public class TypeThrowsWhenDeserialized
    {
    }

    [DataContract]
    [GenerateShape]
    public partial class XAndYProperties
    {
        // We disable SA1300 because we must use lowercase members as required to match the parameter names.
#pragma warning disable SA1300 // Accessible properties should begin with upper-case letter
        [DataMember]
        public int x { get; set; }

        [DataMember]
        public int y { get; set; }
#pragma warning restore SA1300 // Accessible properties should begin with upper-case letter
    }

    [DataContract]
    public class VAndWProperties
    {
        // We disable SA1300 because we must use lowercase members as required to match the parameter names.
#pragma warning disable SA1300 // Accessible properties should begin with upper-case letter
        [DataMember]
        public int v { get; set; }

        [DataMember]
        public int w { get; set; }
#pragma warning restore SA1300 // Accessible properties should begin with upper-case letter
    }

    [DataContract]
    public class XAndYPropertiesWithProgress
    {
        // We disable SA1300 because we must use lowercase members as required to match the parameter names.
#pragma warning disable SA1300 // Accessible properties should begin with upper-case letter
        [DataMember]
        public int x { get; set; }

        [DataMember]
        public int y { get; set; }

        [DataMember]
        public IProgress<int>? p { get; set; }
#pragma warning restore SA1300 // Accessible properties should begin with upper-case letter
    }

    [DataContract]
    public class StrongTypedProgressType
    {
        // We disable SA1300 because we have to match the members of XAndYFieldsWithProgress exactly.
#pragma warning disable SA1300 // Accessible properties should begin with upper-case letter
        [DataMember]
        public int x { get; set; }

        [DataMember]
        public int y { get; set; }

        [DataMember]
        public ProgressWithCompletion<int>? p { get; set; }
#pragma warning restore SA1300 // Accessible properties should begin with upper-case letter
    }

    [DataContract]
    internal class InternalClass
    {
    }

    protected class MockActivityTracingStrategy : IActivityTracingStrategy
    {
        internal Func<JsonRpcRequest, IDisposable>? Inbound { get; set; }

        internal Action<JsonRpcRequest>? Outbound { get; set; }

        public IDisposable? ApplyInboundActivity(JsonRpcRequest request) => this.Inbound?.Invoke(request);

        public void ApplyOutboundActivity(JsonRpcRequest request) => this.Outbound?.Invoke(request);
    }

    /// <summary>
    /// An exception that throws while being serialized.
    /// </summary>
    [Serializable]
    protected class ExceptionMissingDeserializingConstructor : InvalidOperationException
    {
        public ExceptionMissingDeserializingConstructor(string message)
            : base(message)
        {
        }
    }

    [Serializable]
    protected class PrivateSerializableException : Exception
    {
        public PrivateSerializableException()
        {
        }

        public PrivateSerializableException(string message)
            : base(message)
        {
        }

        public PrivateSerializableException(string message, Exception inner)
            : base(message, inner)
        {
        }

#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        protected PrivateSerializableException(
          SerializationInfo info,
          StreamingContext context)
            : base(info, context)
        {
        }
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

        public override void Send(SendOrPostCallback d, object? state)
        {
            throw new NotImplementedException();
        }

        public override void Post(SendOrPostCallback d, object? state)
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
            }).Forget();
        }
    }

    private class BlockingPostSynchronizationContext : SynchronizationContext
    {
        private long postCalls;

        internal ManualResetEventSlim AllowPostToReturn { get; } = new ManualResetEventSlim(false);

        internal AsyncManualResetEvent PostInvoked { get; } = new AsyncManualResetEvent();

        internal long PostCalls => Interlocked.Read(ref this.postCalls);

        public override void Post(SendOrPostCallback d, object? state)
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

    [Serializable]
    private class LyingException : Exception
    {
        public LyingException(string message)
            : base(message)
        {
        }

#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Arrange to change the ClassName value.
            var scratch = new SerializationInfo(info.ObjectType, new FormatterConverter());
            base.GetObjectData(scratch, context);

            foreach (var entry in scratch)
            {
                if (entry.Name != "ClassName")
                {
                    info.AddValue(entry.Name, entry.Value, entry.ObjectType);
                }
            }

            info.AddValue("ClassName", "My.NonExistentException");
        }
    }

    /// <summary>
    /// An exception that does <em>not</em> have a <see cref="SerializableAttribute"/> applied.
    /// </summary>
    private class NonSerializableException : InvalidOperationException
    {
        public NonSerializableException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// An exception that throws while being serialized.
    /// </summary>
    [Serializable]
    private class ThrowOnSerializeException : InvalidOperationException
    {
        public ThrowOnSerializeException(string message)
            : base(message)
        {
        }

#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        protected ThrowOnSerializeException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            // Unlikely to ever be called since serialization throws, but complete the pattern so we test exactly what we mean to.
        }

#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            throw new InvalidOperationException("This exception always throws when serialized.");
        }
    }

    private class JsonRpcThatSubstitutesType : JsonRpc
    {
        public JsonRpcThatSubstitutesType(IJsonRpcMessageHandler messageHandler)
            : base(messageHandler)
        {
        }

        protected override Type? LoadType(string typeFullName, string? assemblyName)
        {
            if (typeFullName == typeof(ArgumentOutOfRangeException).FullName)
            {
                return typeof(ArgumentException);
            }

            return base.LoadType(typeFullName, assemblyName);
        }

        protected override Type? LoadTypeTrimSafe(string typeFullName, string? assemblyName)
        {
            if (typeFullName == typeof(ArgumentOutOfRangeException).FullName)
            {
                return typeof(ArgumentException);
            }

            return base.LoadTypeTrimSafe(typeFullName, assemblyName);
        }
    }

    private class ControlledProgress<T>(Action<T> reported) : IProgress<T>
    {
        public void Report(T value)
        {
            reported(value);
        }
    }

    private class SlowWriteStream : Stream
    {
        private readonly Stream inner;

        internal SlowWriteStream(Stream inner)
        {
            this.inner = inner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => this.inner.Length;

        public override long Position { get => this.inner.Position; set => this.inner.Position = value; }

        internal AsyncAutoResetEvent AllowWrites { get; } = new AsyncAutoResetEvent();

        internal AsyncAutoResetEvent WriteEntered { get; } = new AsyncAutoResetEvent();

        public override void Flush() => this.inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => this.inner.Seek(offset, origin);

        public override void SetLength(long value) => this.inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            this.WriteEntered.Set();
            await this.AllowWrites.WaitAsync(cancellationToken);
            await this.inner.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private record ExceptionFilter : ExceptionSettings
    {
        public ExceptionFilter(int recursionLimit)
            : base(recursionLimit)
        {
        }

        public override bool CanDeserialize(Type type) => typeof(Exception).IsAssignableFrom(type) && !typeof(TaskCanceledException).IsAssignableFrom(type);
    }

    [GenerateShapeFor<StrongTypedProgressType>] // used on client side only
    [GenerateShapeFor<InternalClass>] // Implementation detail of the server method
    [GenerateShapeFor<TypeThrowsWhenSerialized>] // this isn't in any API, but a client tries to serialize it.
    private partial class Witness;
}
