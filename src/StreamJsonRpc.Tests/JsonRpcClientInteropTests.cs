// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcClientInteropTests : InteropTestBase
{
    private readonly JsonRpc clientRpc;

    public JsonRpcClientInteropTests(ITestOutputHelper logger)
        : base(logger, serverTest: false)
    {
        this.clientRpc = new JsonRpc(this.messageHandler);
        this.clientRpc.StartListening();
    }

    [Fact]
    public async Task CancelMessageNotSentAfterResponseIsReceived()
    {
        using (var cts = new CancellationTokenSource())
        {
            Task invokeTask = this.clientRpc.InvokeWithCancellationAsync("test", cancellationToken: cts.Token);
            dynamic request = await this.ReceiveAsync();
            this.Send(new
            {
                jsonrpc = "2.0",
                id = request.id,
                result = new { },
            });
            await invokeTask;

            // Now cancel the request that has already resolved.
            cts.Cancel();

            // Verify that no cancellation message is transmitted.
            await Assert.ThrowsAsync<TaskCanceledException>(() => this.messageHandler.WrittenMessages.DequeueAsync(ExpectedTimeoutToken));
        }
    }

    [Fact]
    public async Task NotifyAsync_ParameterObjectSentAsArray()
    {
        Task notifyTask = this.clientRpc.NotifyAsync("test", new { Bar = "value" });
        JObject request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Equal("value", ((JArray)request["params"])[0]["Bar"].ToString());
    }

    [Fact]
    public async Task NotifyAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.NotifyAsync("test");
        JObject request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Equal(0, ((JArray)request["params"]).Count);
    }

    [Fact]
    public async Task NotifyWithParameterPassedAsObjectAsync_ParameterObjectSentAsObject()
    {
        Task notifyTask = this.clientRpc.NotifyWithParameterObjectAsync("test", new { Bar = "value" });
        JObject request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Object, request["params"].Type);
        Assert.Equal("value", request["params"]["Bar"].ToString());
    }

    [Fact]
    public async Task NotifyWithParameterPassedAsObjectAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.NotifyWithParameterObjectAsync("value");
        JObject request = await this.ReceiveAsync();
        Assert.Null(request["params"]);
    }

    [Fact]
    public async Task InvokeAsync_ParameterObjectSentAsArray()
    {
        Task notifyTask = this.clientRpc.InvokeAsync<object>("test", new { Bar = "value" });
        JObject request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Equal("value", ((JArray)request["params"])[0]["Bar"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.InvokeAsync<object>("test");
        JObject request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Equal(0, ((JArray)request["params"]).Count);
    }

    [Fact]
    public async Task InvokeWithParameterPassedAsObjectAsync_ParameterObjectSentAsObject()
    {
        Task notifyTask = this.clientRpc.InvokeWithParameterObjectAsync<object>("test", new { Bar = "value" });
        JObject request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Object, request["params"].Type);
        Assert.Equal("value", request["params"]["Bar"].ToString());
    }

    [Fact]
    public async Task InvokeWithParameterPassedAsObjectAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.InvokeWithParameterObjectAsync<object>("test");
        JObject request = await this.ReceiveAsync();
        Assert.Null(request["params"]);
    }

    [Fact]
    public void NotifyWithParameterPassedAsObjectAsync_ThrowsExceptions()
    {
        Assert.ThrowsAsync<ArgumentException>(() => this.clientRpc.NotifyWithParameterObjectAsync("test", new int[] { 1, 2 }));
    }

    [Fact]
    public async Task CancelMessageSentAndResponseCompletes()
    {
        using (var cts = new CancellationTokenSource())
        {
            Task<string> invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test", cancellationToken: cts.Token);

            // Wait for the request to actually be transmitted.
            dynamic request = await this.ReceiveAsync();

            // Now cancel the request.
            cts.Cancel();

            dynamic cancellationRequest = await this.ReceiveAsync();
            Assert.Equal("$/cancelRequest", (string)cancellationRequest.method);
            Assert.Equal(request.id, cancellationRequest.@params.id);
            Assert.Null(cancellationRequest.id);

            // Now send the response for the request (which we emulate not responding to cancellation)
            this.Send(new
            {
                jsonrpc = "2.0",
                id = request.id,
                result = "testSucceeded",
            });
            string stringResult = await invokeTask;
            Assert.Equal("testSucceeded", stringResult);
        }
    }
}
