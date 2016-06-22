using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
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
    public async Task CanPassNull()
    {
        var result = await this.clientRpc.InvokeAsync<object>(nameof(Server.MethodThatAccceptsAndReturnsNull), null);
        Assert.Null(result);
        Assert.True(this.server.NullPassed);
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

    public class BaseClass
    {
        public string BaseMethod() => "base";

        public virtual string VirtualBaseMethod() => "base";

        public string RedeclaredBaseMethod() => "base";
    }

    public class Server : BaseClass
    {
        private readonly TaskCompletionSource<string> notificationTcs = new TaskCompletionSource<string>();

        public bool NullPassed { get; private set; }

        public Task<string> NotificationReceived => this.notificationTcs.Task;

        public static string ServerMethod(string argument)
        {
            return argument + "!";
        }

        public override string VirtualBaseMethod() => "child";

        public new string RedeclaredBaseMethod() => "child";

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

        public object MethodThatAccceptsAndReturnsNull(object value)
        {
            this.NullPassed = value == null;
            return null;
        }

        public void NotificationMethod(string arg)
        {
            this.notificationTcs.SetResult(arg);
        }

        public async Task<string> AsyncMethod(string arg)
        {
            await Task.Yield();
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
}
