// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;

public class JsonRpcMethodAttributeTests : TestBase
{
    private const int CustomTaskResult = 100;
    private const string HubName = "TestHub";

    private readonly Server server;
    private readonly Stream serverStream;
    private readonly JsonRpc serverRpc;

    private readonly Stream clientStream;
    private readonly JsonRpc clientRpc;

    public JsonRpcMethodAttributeTests(ITestOutputHelper logger)
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

    [Fact]
    public async Task InvokeAsync_InvokesMethodWithAttributeSet()
    {
        string correctCasingResult = await this.clientRpc.InvokeWithParameterObjectAsync<string>("test/InvokeTestMethod", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("test method attribute", correctCasingResult);

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
        await this.clientRpc.NotifyAsync("base/NotifyVirtualMethodOverride").WithCancellation(this.TimeoutToken);

        Assert.Equal("child NotifyVirtualMethodOverride", await this.server.NotificationReceived.WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task NotifyAsync_NoOverrideMethodNameAttribute()
    {
        await this.clientRpc.NotifyAsync("base/NotifyVirtualMethodNoOverride").WithCancellation(this.TimeoutToken);

        Assert.Equal("child NotifyVirtualMethodNoOverride", await this.server.NotificationReceived.WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task NotifyAsync_OverloadMethodNameAttribute()
    {
        await this.clientRpc.NotifyAsync("notify/OverloadMethodAttribute", "mystring", 1);

        Assert.Equal("string: mystring int: 1", await this.server.NotificationReceived);
    }

    [Fact]
    public async Task CannotCallWithIncorrectSpelling()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("teST/InvokeTestmeTHod"));
    }

    [Fact]
    public async Task CannotCallInvokeClrMethodNameWhenAttributeDefined()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("GetString", "two"));

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("GetStringAsync", "three"));
    }

    [Fact]
    public async Task CanCallWithAttributeNameDefinedOnClrMethodsThatEndWithAsync()
    {
        string asyncResult = await this.clientRpc.InvokeAsync<string>("async/GetString", "one");
        Assert.Equal("async one", asyncResult);
    }

    [Fact]
    public async Task CallingClrMethodsThatHaveAttributeDefinedDoesNotAttemptToMatchImpliedAsync()
    {
        string asyncResult = await this.clientRpc.InvokeAsync<string>("InvokeVirtualMethod", "four");
        Assert.Equal("base four", asyncResult);

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("InvokeVirtualMethodAsync", "four"));
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_ConflictOverloadMethodsThrowsException()
    {
        var serverObject = new ConflictingOverloadServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string result = await clientRpc.InvokeWithCancellationAsync<string>("test/string", ["Andrew"], this.TimeoutToken).WithCancellation(this.TimeoutToken);
        Assert.Equal("conflicting string: Andrew", result);

        result = await clientRpc.InvokeWithCancellationAsync<string>("test/int", ["Andrew", 5], this.TimeoutToken).WithCancellation(this.TimeoutToken);
        Assert.Equal("conflicting string: Andrew int: 5", result);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_MissingAttributeOnOverloadMethodBefore()
    {
        var serverObject = new MissingMethodAttributeOverloadBeforeServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string actual = await clientRpc.InvokeWithCancellationAsync<string>(nameof(MissingMethodAttributeOverloadBeforeServer.InvokeOverloadConflictingMethodAttribute), ["hi"], this.TimeoutToken);
        Assert.Equal("conflicting string: hi", actual);

        string actual2 = await clientRpc.InvokeWithCancellationAsync<string>("test/string", ["hi", 5], this.TimeoutToken);
        Assert.Equal("conflicting string: hi int: 5", actual2);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_MissingAttributeOnOverloadMethodAfter()
    {
        var serverObject = new MissingMethodAttributeOverloadAfterServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string actual = await clientRpc.InvokeWithCancellationAsync<string>("test/string", ["hi"], this.TimeoutToken);
        Assert.Equal("conflicting string: hi", actual);

        string actual2 = await clientRpc.InvokeWithCancellationAsync<string>(nameof(MissingMethodAttributeOverloadAfterServer.InvokeOverloadConflictingMethodAttribute), ["hi", 5], this.TimeoutToken);
        Assert.Equal("conflicting string: hi int: 5", actual2);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_SameAttributeUsedOnDifferentDerivedMethodsThrowsException()
    {
        var serverObject = new SameAttributeUsedOnDifferentDerivedMethodsServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string actual = await clientRpc.InvokeWithCancellationAsync<string>("base/InvokeMethodWithAttribute", ["hi"], this.TimeoutToken);
        Assert.Equal("conflicting string: hi", actual);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_ConflictOverrideMethodsThrowsException()
    {
        var serverObject = new InvalidOverrideServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string actual = await clientRpc.InvokeWithCancellationAsync<string>("child/InvokeVirtualMethodOverride", [], this.TimeoutToken);
        Assert.Equal("child InvokeVirtualMethodOverride", actual);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_ReplacementNameIsAnotherBaseMethodNameServerThrowsException()
    {
        var serverObject = new ReplacementNameIsAnotherBaseMethodNameServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string actual = await clientRpc.InvokeWithCancellationAsync<string>("Second", ["hi"], this.TimeoutToken);
        Assert.Equal("third", actual);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_ReplacementNameIsAnotherMethodNameThrowsException()
    {
        var serverObject = new ReplacementNameIsAnotherMethodNameServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);

        string actual = await clientRpc.InvokeWithCancellationAsync<string>("Second", ["hi"], this.TimeoutToken);

        // The particular method that is invoked isn't well defined, but this one is the one
        // that is invoked today, probably because it's "first" in declaration order.
        Assert.Equal("first hi", actual);
    }

    [Fact]
    public async Task JsonRpcMethodAttribute_InvalidAsyncMethodWithAsyncAddedInAttributeThrowsException()
    {
        var serverObject = new CollidingAsyncMethodWithAsyncAddedInAttributeServer();

        (Stream clientStream, Stream serverStream) = Nerdbank.FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(serverStream, serverObject);
        JsonRpc clientRpc = JsonRpc.Attach(clientStream);
        string result = await clientRpc.InvokeWithCancellationAsync<string>("FirstAsync", ["Andrew"], this.TimeoutToken).WithCancellation(this.TimeoutToken);

        // The particular method that is invoked isn't well defined, but this one is the one
        // that is invoked today, probably because it's "first" in declaration order.
        Assert.Equal("firstAsync Andrew", result);
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
    public async Task JsonRpcMethodAttribute_SwappingAsyncMethodServerThrowsException()
    {
        var invalidServer = new SwappingAsyncMethodServer();

        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        var serverStream = streams.Item1;
        var clientStream = streams.Item2;

        var rpc = JsonRpc.Attach(serverStream, clientStream, invalidServer);

        string result = await rpc.InvokeAsync<string>("FirstAsync", "hi");
        Assert.Equal("First hi", result);

        result = await rpc.InvokeAsync<string>("First", "bye");
        Assert.Equal("FirstAsync bye", result);
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

#pragma warning disable CA1801 // Review unused parameters
    public class BaseClass
    {
        protected readonly TaskCompletionSource<string> notificationTcs = new TaskCompletionSource<string>();

        [JsonRpcMethod("base/InvokeMethodWithAttribute")]
        public string InvokeMethodWithAttribute() => $"base {nameof(this.InvokeMethodWithAttribute)}";

        [JsonRpcMethod("base/InvokeVirtualMethodOverride")]
        public virtual string InvokeVirtualMethodOverride() => $"base {nameof(this.InvokeVirtualMethodOverride)}";

        [JsonRpcMethod("base/InvokeVirtualMethodNoOverride")]
        public virtual string InvokeVirtualMethodNoOverride() => $"base {nameof(this.InvokeVirtualMethodNoOverride)}";

        [JsonRpcMethod("base/NotifyMethodWithAttribute")]
        public void NotifyMethodWithAttribute()
        {
            this.notificationTcs.SetResult($"base {nameof(this.NotifyMethodWithAttribute)}");
        }

        [JsonRpcMethod("base/NotifyVirtualMethodOverride")]
        public virtual void NotifyVirtualMethodOverride()
        {
            this.notificationTcs.SetResult($"base {nameof(this.NotifyVirtualMethodOverride)}");
        }

        [JsonRpcMethod("base/NotifyVirtualMethodNoOverride")]
        public virtual void NotifyVirtualMethodNoOverride()
        {
            this.notificationTcs.SetResult($"base {nameof(this.NotifyVirtualMethodNoOverride)}");
        }

        [JsonRpcMethod("InvokeVirtualMethod")]
        public async virtual Task<string> InvokeVirtualMethodAsync(string arg)
        {
            await Task.Yield();
            return $"base {arg}";
        }
    }

    /// <summary>
    /// This class has a derived method with a different <see cref="JsonRpcMethodAttribute" /> value from its base.
    /// </summary>
    public class InvalidOverrideServer : BaseClass
    {
        [JsonRpcMethod("child/InvokeVirtualMethodOverride")]
        public override string InvokeVirtualMethodOverride() => $"child {nameof(this.InvokeVirtualMethodOverride)}";
    }

    /// <summary>
    /// This class has overloaded methods with different <see cref="JsonRpcMethodAttribute" /> values.
    /// </summary>
    public class ConflictingOverloadServer : BaseClass
    {
        [JsonRpcMethod("test/string")]
        public string InvokeOverloadConflictingMethodAttribute(string test) => $"conflicting string: {test}";

        [JsonRpcMethod("test/int")]
        public string InvokeOverloadConflictingMethodAttribute(string arg1, int arg2) => $"conflicting string: {arg1} int: {arg2}";
    }

    /// <summary>
    /// This class has an overloaded method that's missing a <see cref="JsonRpcMethodAttribute" />.
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
    /// This class has two different methods in the base and derived classes that have the same <see cref="JsonRpcMethodAttribute" /> value.
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
        public string First(string test) => $"first {test}";

        public string Second(string test) => $"second {test}";
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

    public class CollidingAsyncMethodWithAsyncAddedInAttributeServer
    {
        public async virtual Task<string> FirstAsync(string arg)
        {
            await Task.Yield();
            return $"firstAsync {arg}";
        }

        [JsonRpcMethod("FirstAsync")]
        public async virtual Task<string> First(string arg)
        {
            await Task.Yield();
            return $"first {arg}";
        }
    }

    public class SwappingAsyncMethodServer
    {
        [JsonRpcMethod("First")]
        public async virtual Task<string> FirstAsync(string arg)
        {
            await Task.Yield();
            return $"FirstAsync {arg}";
        }

        [JsonRpcMethod("FirstAsync")]
        public async virtual Task<string> First(string arg)
        {
            await Task.Yield();
            return $"First {arg}";
        }
    }

#pragma warning disable CA1822 // Mark members as static
    public class Server : BaseClass
    {
        public Task<string> NotificationReceived => this.notificationTcs.Task;

        [JsonRpcMethod("test/InvokeTestMethod")]
        public string InvokeTestMethodAttribute() => "test method attribute";

        [JsonRpcMethod("base/InvokeVirtualMethodOverride")]
        public override string InvokeVirtualMethodOverride() => $"child {nameof(this.InvokeVirtualMethodOverride)}";

        public override string InvokeVirtualMethodNoOverride() => $"child {nameof(this.InvokeVirtualMethodNoOverride)}";

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
            this.notificationTcs.SetResult($"child {nameof(this.NotifyVirtualMethodOverride)}");
        }

        [JsonRpcMethod("fiRst")]
        public string First(string test) => "first";

        [JsonRpcMethod("Second")]
        public string Second(string test) => "second";

        [JsonRpcMethod("second")]
        public string Third(string test) => "third";

        public override void NotifyVirtualMethodNoOverride()
        {
            this.notificationTcs.SetResult($"child {nameof(this.NotifyVirtualMethodNoOverride)}");
        }

        [JsonRpcMethod("async/GetString")]
        public async Task<string> GetStringAsync(string arg)
        {
            await Task.Yield();

            return "async " + arg;
        }
#pragma warning restore CA1822 // Mark members as static
    }
}
