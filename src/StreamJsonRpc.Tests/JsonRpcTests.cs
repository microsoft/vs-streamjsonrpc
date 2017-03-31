using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcTests : TestBase
{
    private const int CustomTaskResult = 100;
    private const string HubName = "TestHub";

    private readonly Server server;
    private readonly Stream serverStream;
    private readonly JsonRpc serverRpc;

    private readonly Stream clientStream;
    private readonly JsonRpc clientRpc;

    public JsonRpcTests(ITestOutputHelper logger)
        : base(logger)
    {
        TaskCompletionSource<JsonRpc> serverRpcTcs = new TaskCompletionSource<JsonRpc>();

        this.server = new Server();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = JsonRpc.Attach(this.serverStream, this.server);
        this.clientRpc = JsonRpc.Attach(this.clientStream);
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

        base.Dispose(disposing);
    }

    [Fact]
    public void Attach_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JsonRpc.Attach(stream: null));
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

        var helperHandler = new HeaderDelimitedMessageHandler(streams.Item2, null);
        await helperHandler.WriteAsync(JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            method = nameof(Server.NotificationMethod),
            @params = new[] { "hello" },
        }), this.TimeoutToken);

        Assert.Equal("hello", await server.NotificationReceived.WithCancellation(this.TimeoutToken));

        // Any form of outbound transmission should be rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.NotifyAsync("foo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.InvokeAsync("foo"));

        Assert.False(disconnected.IsSet);

        // Receiving a request should forcibly terminate the stream.
        await helperHandler.WriteAsync(JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = nameof(Server.MethodThatAccceptsAndReturnsNull),
            @params = new object[] { null },
        }), this.TimeoutToken);

        // The connection should be closed because we can't send a response.
        await disconnected.WaitAsync().WithCancellation(this.TimeoutToken);

        // The method should not have been invoked.
        Assert.False(server.NullPassed);
    }

    [Fact]
    public async Task Attach_NullReceivingStream_CanOnlySendNotifications()
    {
        var sendingStream = new MemoryStream();
        long lastPosition = sendingStream.Position;
        var rpc = JsonRpc.Attach(sendingStream: sendingStream, receivingStream: null);

        // Sending notifications is fine, as it's an outbound-only communication.
        await rpc.NotifyAsync("foo");
        Assert.NotEqual(lastPosition, sendingStream.Position);

        // Sending requests should not be allowed, since it requires waiting for a response.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.InvokeAsync("foo"));
    }

    [Fact]
    public async Task CanInvokeMethodOnServer()
    {
        string TestLine = "TestLine1" + new string('a', 1024 * 1024);
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.ServerMethod), TestLine);
        Assert.Equal(TestLine + "!", result1);
    }

    [Fact]
    public async Task CanInvokeTaskMethodOnServer()
    {
        await this.clientRpc.InvokeAsync(nameof(Server.ServerMethodThatReturnsTask));
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
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.ServerMethodThatReturnsCancelledTask)));
        Assert.Null(exception.RemoteErrorCode);
        Assert.Null(exception.RemoteStackTrace);
    }

    [Fact]
    public async Task CanInvokeMethodThatReturnsTaskOfInternalClass()
    {
        // JSON RPC cannot invoke non-public members. A public member cannot have Task<NonPublicType> result.
        // Though it can have result of just Task type, and return a Task<NonPublicType>, and dev hub supports that.
        InternalClass result = await this.clientRpc.InvokeAsync<InternalClass>(nameof(Server.MethodThatReturnsTaskOfInternalClass));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CanPassExceptionFromServer()
    {
        const int COR_E_UNAUTHORIZEDACCESS = unchecked((int)0x80070005);
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.NotNull(exception.RemoteStackTrace);
        Assert.StrictEqual(COR_E_UNAUTHORIZEDACCESS.ToString(CultureInfo.InvariantCulture), exception.RemoteErrorCode);
    }

    [Fact]
    public async Task CanPassAndCallPrivateMethodsObjects()
    {
        var result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new Foo { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);

        result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);
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
        var result = await this.clientRpc.InvokeAsync<object>(nameof(Server.MethodThatAccceptsAndReturnsNull), new object[] { null });
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
        await this.clientRpc.NotifyAsync(nameof(Server.NotificationMethod), "foo");
        Assert.Equal("foo", await this.server.NotificationReceived);
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
        Assert.NotNull(exception.RemoteStackTrace);
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
        await Assert.ThrowsAsync(typeof(RemoteMethodNotFoundException), () => this.clientRpc.InvokeAsync("missingMethod", 50));
        await Assert.ThrowsAsync(typeof(RemoteMethodNotFoundException), () => this.clientRpc.InvokeAsync(nameof(Server.OverloadedMethod), new { X = 100 }));
    }

    [Fact]
    public async Task ThrowsIfTargetNotSet()
    {
        await Assert.ThrowsAsync(typeof(RemoteTargetNotSetException), () => this.serverRpc.InvokeAsync(nameof(Server.OverloadedMethod)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectedEventIsFired(bool disposeRpc)
    {
        var disconnectedEventFired = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();

        // Subscribe to disconnected event
        object disconnectedEventSender = null;
        this.serverRpc.Disconnected += delegate (object sender, JsonRpcDisconnectedEventArgs e)
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
        this.serverRpc.Disconnected += delegate (object sender, JsonRpcDisconnectedEventArgs e)
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
    public async Task CannotCallPrivateMethod()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync(nameof(Server.InternalMethod), 10));
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
    public void SetEncodingToNullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => this.clientRpc.Encoding = null);
        Assert.NotNull(this.clientRpc.Encoding);
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

    [Fact]
    public async Task CancelMessageSentWhileAwaitingResponse()
    {
        using (var cts = new CancellationTokenSource())
        {
            var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(Server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token);
            await this.server.ServerMethodReached.WaitAsync(this.TimeoutToken);
            cts.Cancel();

            // Ultimately, the server throws because it was canceled.
            await Assert.ThrowsAsync<RemoteInvocationException>(() => invokeTask.WithTimeout(UnexpectedTimeout));
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
    public async Task InvokeWithPrecanceledToken()
    {
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => this.clientRpc.InvokeWithCancellationAsync(nameof(server.AsyncMethodIgnoresCancellation), new[] { "a" }, cts.Token));
        }
    }

    [Fact]
    public async Task InvokeThenCancelToken()
    {
        using (var cts = new CancellationTokenSource())
        {
            this.server.AllowServerMethodToReturn.Set();
            await this.clientRpc.InvokeWithCancellationAsync(nameof(server.AsyncMethodWithCancellation), new[] { "a" }, cts.Token);
            cts.Cancel();
        }
    }

    [Fact]
    public async Task UnserializableTypeWorksWithConverter()
    {
        this.clientRpc.JsonSerializer.Converters.Add(new UnserializableTypeConverter());
        this.serverRpc.JsonSerializer.Converters.Add(new UnserializableTypeConverter());
        var result = await this.clientRpc.InvokeAsync<UnserializableType>(nameof(server.RepeatSpecialType), new UnserializableType { Value = "a" });
        Assert.Equal("a!", result.Value);
    }

    [Fact]
    public async Task CustomJsonConvertersAreNotAppliedToBaseMessage()
    {
        // This test works because it encodes any string value, such that if the json-rpc "method" property
        // were serialized using the same serializer as parameters, the invocation would fail because the server-side
        // doesn't find the method with the mangled name.

        // Test with the converter only on the client side.
        this.clientRpc.JsonSerializer.Converters.Add(new StringBase64Converter());
        string result = await this.clientRpc.InvokeAsync<string>(nameof(server.ExpectEncodedA), "a");
        Assert.Equal("a", result);

        // Test with the converter on both sides.
        this.serverRpc.JsonSerializer.Converters.Add(new StringBase64Converter());
        result = await this.clientRpc.InvokeAsync<string>(nameof(server.RepeatString), "a");
        Assert.Equal("a", result);

        // Test with the converter only on the server side.
        this.clientRpc.JsonSerializer.Converters.Clear();
        result = await this.clientRpc.InvokeAsync<string>(nameof(server.AsyncMethod), "YQ==");
        Assert.Equal("YSE=", result); // a!
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_UncancellableMethodWithoutCancellationToken()
    {
        await CheckGCPressureAsync(
            async delegate
            {
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(server.AsyncMethod), new object[] { "a" }));
            });
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_UncancellableMethodWithCancellationToken()
    {
        var cts = new CancellationTokenSource();
        await CheckGCPressureAsync(
            async delegate
            {
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(server.AsyncMethod), new object[] { "a" }, cts.Token));
            });
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_CancellableMethodWithoutCancellationToken()
    {
        await CheckGCPressureAsync(
            async delegate
            {
                this.server.AllowServerMethodToReturn.Set();
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(server.AsyncMethodWithCancellation), new object[] { "a" }, CancellationToken.None));
            });
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_CancellableMethodWithCancellationToken()
    {
        var cts = new CancellationTokenSource();
        await CheckGCPressureAsync(
            async delegate
            {
                this.server.AllowServerMethodToReturn.Set();
                Assert.Equal("a!", await this.clientRpc.InvokeWithCancellationAsync<string>(nameof(server.AsyncMethodWithCancellation), new object[] { "a" }, cts.Token));
            });
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task InvokeWithCancellationAsync_CancellableMethodWithCancellationToken_Canceled()
    {
        await CheckGCPressureAsync(
            async delegate
            {
                var cts = new CancellationTokenSource();
                var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>(nameof(server.AsyncMethodWithCancellation), new object[] { "a" }, cts.Token);
                cts.Cancel();
                this.server.AllowServerMethodToReturn.Set();
                await invokeTask.NoThrowAwaitable(); // may or may not throw due to cancellation (and its inherent race condition)
            });
    }

    [Fact]
    public async Task CanInvokeServerMethodWithParameterPassedAsObject()
    {
        string result1 = await this.clientRpc.InvokeWithParameterObjectAsync<string>(nameof(Server.TestParameter), new { test = "test" });
        Assert.Equal("object {\r\n  \"test\": \"test\"\r\n}", result1);
    }

    [Fact]
    public async Task CanInvokeServerMethodWithParameterPassedAsArray()
    {
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.TestParameter), "test");
        Assert.Equal("object test", result1);
    }

    [Fact]
    public async Task CanInvokeServerMethodWithNoParameterPassedAsObject()
    {
        string result1 = await this.clientRpc.InvokeWithParameterObjectAsync<string>(nameof(Server.TestParameter));
        Assert.Equal("object or array", result1);
    }

    [Fact]
    public async Task CanInvokeServerMethodWithNoParameterPassedAsArray()
    {
        string result1 = await this.clientRpc.InvokeAsync<string>(nameof(Server.TestParameter));
        Assert.Equal("object or array", result1);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionThrownIfServerHasMutlipleMethodsMatched()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>(nameof(Server.TestInvalidMethod)));
    }

    [Fact]
    public async Task InvokeAsync_InvokesMethodWithAttributeSet()
    {
        string correctCasingResult = await this.clientRpc.InvokeWithParameterObjectAsync<string>("test/InvokeTestMethod");
        Assert.Equal("test method attribute", correctCasingResult);

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("teST/InvokeTestmeTHod"));

        string baseMethodResult = await this.clientRpc.InvokeAsync<string>("base/InvokeMethodWithAttribute");
        Assert.Equal("base InvokeMethodWithAttribute", baseMethodResult);

        string baseOverrideResult = await this.clientRpc.InvokeAsync<string>("base/InvokeVirtualMethodOverride");
        Assert.Equal("child InvokeVirtualMethodOverride", baseOverrideResult);

        string baseNoOverrideResult = await this.clientRpc.InvokeAsync<string>("base/InvokeVirtualMethodNoOverride");
        Assert.Equal("child InvokeVirtualMethodNoOverride", baseNoOverrideResult);

        string stringOverloadResult = await this.clientRpc.InvokeAsync<string>("test/OverloadMethodAttribute", "mystring");
        Assert.Equal("string: mystring", stringOverloadResult);

        string intOverloadResult = await this.clientRpc.InvokeAsync<string>("test/OverloadMethodAttribute", "mystring", 1);
        Assert.Equal("string: mystring int: 1", intOverloadResult);

        string replacementCasingNotMatchResult = await this.clientRpc.InvokeAsync<string>("fiRst", "test");
        Assert.Equal("first", replacementCasingNotMatchResult);

        string replacementCasingMatchResult = await this.clientRpc.InvokeAsync<string>("Second", "test");
        Assert.Equal("second", replacementCasingMatchResult);

        string replacementDoesNotMatchResult = await this.clientRpc.InvokeAsync<string>("second", "test");
        Assert.Equal("third", replacementDoesNotMatchResult);

        string asyncResult = await this.clientRpc.InvokeAsync<string>("async/GetString", "one");
        Assert.Equal("async one", asyncResult);

        asyncResult = await this.clientRpc.InvokeAsync<string>("GetString", "two");
        Assert.Equal("async two", asyncResult);

        asyncResult = await this.clientRpc.InvokeAsync<string>("GetStringAsync", "three");
        Assert.Equal("async three", asyncResult);

        asyncResult = await this.clientRpc.InvokeAsync<string>("InvokeVirtualMethod", "four");
        Assert.Equal("base four", asyncResult);
    }

    [Fact]
    public async Task NotifyAsync_InvokesMethodWithAttributeSet()
    {
        await this.clientRpc.NotifyAsync("test/NotifyTestMethod");
        Assert.Equal("test method attribute", await this.server.NotificationReceived);
    }

    [Fact]
    public async Task NotifyAsync_MethodNameAttributeCasing()
    {
        await this.clientRpc.NotifyWithParameterObjectAsync("teST/NotifyTestmeTHod");
        await this.clientRpc.NotifyAsync("base/NotifyMethodWithAttribute");

        Assert.Equal("base NotifyMethodWithAttribute", await this.server.NotificationReceived);
    }

    [Fact]
    public async Task NotifyAsync_OverrideMethodNameAttribute()
    {
        await this.clientRpc.NotifyAsync("base/NotifyVirtualMethodOverride");

        Assert.Equal("child NotifyVirtualMethodOverride", await this.server.NotificationReceived);
    }

    [Fact]
    public async Task NotifyAsync_NoOverrideMethodNameAttribute()
    {
        await this.clientRpc.NotifyAsync("base/NotifyVirtualMethodNoOverride");

        Assert.Equal("child NotifyVirtualMethodNoOverride", await this.server.NotificationReceived);
    }

    [Fact]
    public async Task NotifyAsync_OverloadMethodNameAttribute()
    {
        await this.clientRpc.NotifyAsync("notify/OverloadMethodAttribute", "mystring", 1);

        Assert.Equal("string: mystring int: 1", await this.server.NotificationReceived);
    }

    [Fact]
    public void JsonRpcMethodAttribute_ConflictOverloadMethodsThrowsException()
    {
        var invalidServer = new ConflictingOverloadServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_MissingAttributeOnOverloadMethodBeforeThrowsException()
    {
        var invalidServer = new MissingMethodAttributeOverloadBeforeServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_MissingAttributeOnOverloadMethodAfterThrowsException()
    {
        var invalidServer = new MissingMethodAttributeOverloadAfterServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_SameAttributeUsedOnDifferentDerivedMethodsThrowsException()
    {
        var invalidServer = new SameAttributeUsedOnDifferentDerivedMethodsServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_SameAttributeUsedOnDifferentMethodsThrowsException()
    {
        var invalidServer = new SameAttributeUsedOnDifferentMethodsServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_ConflictOverrideMethodsThrowsException()
    {
        var invalidServer = new InvalidOverrideServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_ReplacementNameIsAnotherBaseMethodNameServerThrowsException()
    {
        var invalidServer = new ReplacementNameIsAnotherBaseMethodNameServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_ReplacementNameIsAnotherMethodNameThrowsException()
    {
        var invalidServer = new ReplacementNameIsAnotherMethodNameServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_InvalidAsyncMethodWithAsyncAddedInAttributeThrowsException()
    {
        var invalidServer = new InvalidAsyncMethodWithAsyncAddedInAttributeServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_InvalidAsyncMethodWithAsyncRemovedInAttributeThrowsException()
    {
        var invalidServer = new InvalidAsyncMethodWithAsyncRemovedInAttributeServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
    }

    [Fact]
    public void JsonRpcMethodAttribute_InvalidAsyncMethodServerThrowsException()
    {
        var invalidServer = new InvalidAsyncMethodServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        Assert.Throws<ArgumentException>(() => JsonRpc.Attach(serverStream, clientStream, invalidServer));
        Assert.Throws<ArgumentException>(() => new JsonRpc(serverStream, clientStream, invalidServer));
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

    public class BaseClass
    {
        protected readonly TaskCompletionSource<string> notificationTcs = new TaskCompletionSource<string>();

        public string BaseMethod() => "base";

        public virtual string VirtualBaseMethod() => "base";

        public string RedeclaredBaseMethod() => "base";

        [JsonRpcMethod("base/InvokeMethodWithAttribute")]
        public string InvokeMethodWithAttribute() => $"base {nameof(InvokeMethodWithAttribute)}";

        [JsonRpcMethod("base/InvokeVirtualMethodOverride")]
        public virtual string InvokeVirtualMethodOverride() => $"base {nameof(InvokeVirtualMethodOverride)}";

        [JsonRpcMethod("base/InvokeVirtualMethodNoOverride")]
        public virtual string InvokeVirtualMethodNoOverride() => $"base {nameof(InvokeVirtualMethodNoOverride)}";

        [JsonRpcMethod("base/NotifyMethodWithAttribute")]
        public void NotifyMethodWithAttribute()
        {
            this.notificationTcs.SetResult($"base {nameof(NotifyMethodWithAttribute)}");
        }

        [JsonRpcMethod("base/NotifyVirtualMethodOverride")]
        public virtual void NotifyVirtualMethodOverride()
        {
            this.notificationTcs.SetResult($"base {nameof(NotifyVirtualMethodOverride)}");
        }

        [JsonRpcMethod("base/NotifyVirtualMethodNoOverride")]
        public virtual void NotifyVirtualMethodNoOverride()
        {
            this.notificationTcs.SetResult($"base {nameof(NotifyVirtualMethodNoOverride)}");
        }

        [JsonRpcMethod("InvokeVirtualMethod")]
        public async virtual Task<string> InvokeVirtualMethodAsync(string arg)
        {
            await Task.Yield();
            return $"base {arg}";
        }
    }

    /// <summary>
    /// This class is invalid because a derived method has a different <see cref="JsonRpcMethodAttribute" /> value.
    /// </summary>
    public class InvalidOverrideServer : BaseClass
    {
        [JsonRpcMethod("child/InvokeVirtualMethodOverride")]
        public override string InvokeVirtualMethodOverride() => $"child {nameof(InvokeVirtualMethodOverride)}";
    }

    /// <summary>
    /// This class is invalid because overloaded methods have different <see cref="JsonRpcMethodAttribute" /> values.
    /// </summary>
    public class ConflictingOverloadServer : BaseClass
    {
        [JsonRpcMethod("test/string")]
        public string InvokeOverloadConflictingMethodAttribute(string test) => $"conflicting string: {test}";

        [JsonRpcMethod("test/int")]
        public string InvokeOverloadConflictingMethodAttribute(string arg1, int arg2) => $"conflicting string: {arg1} int: {arg2}";
    }

    /// <summary>
    /// This class is invalid because an overloaded method is missing <see cref="JsonRpcMethodAttribute" /> value.
    /// The method missing the attribute comes before the method with the attribute.
    /// </summary>
    public class MissingMethodAttributeOverloadBeforeServer : BaseClass
    {
        public string InvokeOverloadConflictingMethodAttribute(string test) => $"conflicting string: {test}";

        [JsonRpcMethod("test/string")]
        public string InvokeOverloadConflictingMethodAttribute(string arg1, int arg2) => $"conflicting string: {arg1} int: {arg2}";
    }

    /// <summary>
    /// This class is invalid because an overloaded method is missing <see cref="JsonRpcMethodAttribute" /> value.
    /// The method missing the attribute comes after the method with the attribute.
    /// </summary>
    public class MissingMethodAttributeOverloadAfterServer : BaseClass
    {
        [JsonRpcMethod("test/string")]
        public string InvokeOverloadConflictingMethodAttribute(string test) => $"conflicting string: {test}";

        public string InvokeOverloadConflictingMethodAttribute(string arg1, int arg2) => $"conflicting string: {arg1} int: {arg2}";
    }

    /// <summary>
    /// This class is invalid because two different methods in the same class have the same <see cref="JsonRpcMethodAttribute" /> value.
    /// </summary>
    public class SameAttributeUsedOnDifferentMethodsServer : BaseClass
    {
        [JsonRpcMethod("test/string")]
        public string First(string test) => $"conflicting string: {test}";

        [JsonRpcMethod("test/string")]
        public string Second(string arg1, int arg2) => $"conflicting string: {arg1} int: {arg2}";
    }

    /// <summary>
    /// This class is invalid because two different methods in the base and derived classes have the same <see cref="JsonRpcMethodAttribute" /> value.
    /// </summary>
    public class SameAttributeUsedOnDifferentDerivedMethodsServer : BaseClass
    {
        [JsonRpcMethod("base/InvokeMethodWithAttribute")]
        public string First(string test) => $"conflicting string: {test}";
    }

    public class Base
    {
        [JsonRpcMethod("base/first")]
        public virtual string First(string test) => "first";

        public string Second() => "Second";
    }

    public class ReplacementNameIsAnotherBaseMethodNameServer : Base
    {
        public override string First(string test) => "first";

        [JsonRpcMethod("Second")]
        public string Third(string test) => "third";
    }

    public class ReplacementNameIsAnotherMethodNameServer
    {
        [JsonRpcMethod("Second")]
        public string First(string test) => "first";

        public string Second(string test) => "second";
    }

    public class InvalidAsyncMethodWithAsyncRemovedInAttributeServer
    {
        [JsonRpcMethod("First")]
        public async virtual Task<string> FirstAsync(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }

        public async virtual Task<string> First(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }
    }

    public class InvalidAsyncMethodWithAsyncAddedInAttributeServer
    {
        public async virtual Task<string> FirstAsync(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }

        [JsonRpcMethod("FirstAsync")]
        public async virtual Task<string> First(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }
    }

    public class InvalidAsyncMethodServer
    {
        [JsonRpcMethod("First")]
        public async virtual Task<string> FirstAsync(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }

        [JsonRpcMethod("FirstAsync")]
        public async virtual Task<string> First(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }
    }

    public class Server : BaseClass
    {
        public bool NullPassed { get; private set; }

        public AsyncAutoResetEvent AllowServerMethodToReturn { get; } = new AsyncAutoResetEvent();

        public AsyncAutoResetEvent ServerMethodReached { get; } = new AsyncAutoResetEvent();

        public Task<string> NotificationReceived => this.notificationTcs.Task;

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

        public override string VirtualBaseMethod() => "child";

        public new string RedeclaredBaseMethod() => "child";

        [JsonRpcMethod("test/InvokeTestMethod")]
        public string InvokeTestMethodAttribute() => "test method attribute";

        [JsonRpcMethod("base/InvokeVirtualMethodOverride")]
        public override string InvokeVirtualMethodOverride() => $"child {nameof(InvokeVirtualMethodOverride)}";

        public override string InvokeVirtualMethodNoOverride() => $"child {nameof(InvokeVirtualMethodNoOverride)}";

        [JsonRpcMethod("test/OverloadMethodAttribute")]
        public string InvokeOverloadMethodAttribute(string test) => $"string: {test}";

        [JsonRpcMethod("test/OverloadMethodAttribute")]
        public string InvokeOverloadMethodAttribute(string arg1, int arg2) => $"string: {arg1} int: {arg2}";

        [JsonRpcMethod("notify/OverloadMethodAttribute")]
        public void NotifyOverloadMethodAttribute(string test)
        {
            this.notificationTcs.SetResult($"string: {test}");
        }

        [JsonRpcMethod("notify/OverloadMethodAttribute")]
        public void NotifyOverloadMethodAttribute(string arg1, int arg2)
        {
            this.notificationTcs.SetResult($"string: {arg1} int: {arg2}");
        }

        [JsonRpcMethod("test/NotifyTestMethod")]
        public void NotifyTestMethodAttribute()
        {
            this.notificationTcs.SetResult($"test method attribute");
        }

        [JsonRpcMethod("base/NotifyVirtualMethodOverride")]
        public override void NotifyVirtualMethodOverride()
        {
            this.notificationTcs.SetResult($"child {nameof(NotifyVirtualMethodOverride)}");
        }

        [JsonRpcMethod("fiRst")]
        public string First(string test) => "first";

        [JsonRpcMethod("Second")]
        public string Second(string test) => "second";

        [JsonRpcMethod("second")]
        public string Third(string test) => "third";

        public override void NotifyVirtualMethodNoOverride()
        {
            this.notificationTcs.SetResult($"child {nameof(NotifyVirtualMethodNoOverride)}");
        }

        [JsonRpcMethod("async/GetString")]
        public async Task<string> GetStringAsync(string arg)
        {
            await Task.Yield();

            return "async " + arg;
        }

        internal void InternalMethod()
        {
        }

        public Task ServerMethodThatReturnsCustomTask()
        {
            var result = new CustomTask();
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

        public static int MethodWithDefaultParameter(int x, int y = 10)
        {
            return x + y;
        }

        public object MethodThatAcceptsNothingAndReturnsNull()
        {
            return null;
        }

        public object MethodThatAccceptsAndReturnsNull(object value)
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

        public async Task<string> AsyncMethodWithCancellation(string arg, CancellationToken cancellationToken)
        {
            this.ServerMethodReached.Set();
            await this.AllowServerMethodToReturn.WaitAsync(cancellationToken);
            return arg + "!";
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

        public async Task AsyncMethodThatThrows()
        {
            await Task.Yield();
            throw new Exception();
        }

        public Task MethodThatReturnsTaskOfInternalClass()
        {
            var result = new Task<InternalClass>(() => new InternalClass());
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
    }

    public class Foo
    {
        [JsonProperty(Required = Required.Always)]
        public string Bar { get; set; }
        public int Bazz { get; set; }
    }

    private class CustomTask : Task<int>
    {
        public CustomTask() : base(() => 0) { }

        public new int Result { get { return CustomTaskResult; } }
    }

    internal class InternalClass
    {
    }
    public class UnserializableType
    {
        [JsonIgnore]
        public string Value { get; set; }
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

    private class StringBase64Converter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String((string)reader.Value));
            return decoded;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var stringValue = (string)value;
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(stringValue));
            writer.WriteValue(encoded);
        }
    }
}
