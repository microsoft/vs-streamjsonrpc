// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

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
        string correctCasingResult = await this.clientRpc.InvokeWithParameterObjectAsync<string>("test/InvokeTestMethod");
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

    public async Task CannotCallWithIncorrectSpelling()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("teST/InvokeTestmeTHod"));
    }

    public async Task CannotCallInvokeClrMethodNameWhenAttributeDefined()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("GetString", "two"));

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("GetStringAsync", "three"));
    }

    public async Task CanCallWithAttributeNameDefinedOnClrMethodsThatEndWithAsync()
    {
        string asyncResult = await this.clientRpc.InvokeAsync<string>("async/GetString", "one");
        Assert.Equal("async one", asyncResult);
    }

    public async Task CallingClrMethodsThatHaveAttributeDefinedDoesNotAttemptToMatchImpliedAsync()
    {
        string asyncResult = await this.clientRpc.InvokeAsync<string>("InvokeVirtualMethod", "four");
        Assert.Equal("base four", asyncResult);

        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeAsync<string>("InvokeVirtualMethodAsync", "four"));
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
    /// This class is invalid because a derived method has a different <see cref="JsonRpcMethodAttribute" /> value.
    /// </summary>
    public class InvalidOverrideServer : BaseClass
    {
        [JsonRpcMethod("child/InvokeVirtualMethodOverride")]
        public override string InvokeVirtualMethodOverride() => $"child {nameof(this.InvokeVirtualMethodOverride)}";
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
    }
}
