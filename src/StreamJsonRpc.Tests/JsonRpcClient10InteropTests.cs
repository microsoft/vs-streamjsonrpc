// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Verifies the <see cref="JsonRpc"/> class's functionality as a JSON-RPC 1.0 *client* (i.e. the one sending requests, and receiving results)
/// against various server messages.
/// </summary>
public class JsonRpcClient10InteropTests : InteropTestBase
{
    private readonly JsonRpc clientRpc;

    public JsonRpcClient10InteropTests(ITestOutputHelper logger)
        : base(logger, serverTest: false)
    {
        this.messageHandler.Formatter.ProtocolVersion = new Version(1, 0);
        this.clientRpc = new JsonRpc(this.messageHandler);
        this.clientRpc.StartListening();
    }

    [Fact]
    public async Task ClientRecognizesResultWithNullError()
    {
        var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test", new object[] { "arg1" }, this.TimeoutToken);

        // Receive the request at the server side and sanity check its contents.
        JToken request = await this.ReceiveAsync();
        Assert.Equal("test", request.Value<string>("method"));
        Assert.Single((JArray)request["params"]);
        Assert.Equal("arg1", request["params"][0].Value<string>());
        Assert.NotNull(request["id"]);

        const string expectedResult = "some result";
        this.Send(new
        {
            id = request["id"],
            result = expectedResult,
            error = (object?)null, // JSON-RPC 1.0 requires that the error property be specified as null in successful responses.
        });
        string actualResult = await invokeTask;
        Assert.Equal(expectedResult, actualResult);
    }

    [Fact]
    public async Task ClientRecognizesErrorWithNullResult()
    {
        var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test");
        JToken request = await this.ReceiveAsync();
        const string expectedErrorMessage = "some result";
        this.Send(new
        {
            id = request["id"],
            result = (object?)null, // JSON-RPC 1.0 requires that result be specified and null if there is an error.
            error = new { message = expectedErrorMessage },
        });
        var error = await Assert.ThrowsAsync<RemoteInvocationException>(() => invokeTask);
        Assert.Equal(expectedErrorMessage, error.Message);
    }

    /// <summary>
    /// Verifies that since JSON-RPC 1.0 doesn't support parameter objects (only parameter arrays are allowed),
    /// no request is sent from the client that violates that.
    /// </summary>
    [Fact]
    public async Task ClientThrowsOnAttemptToSendParamsObject()
    {
        var ex = await Assert.ThrowsAsync<JsonSerializationException>(() => this.clientRpc.InvokeWithParameterObjectAsync("test", new { something = 3 }, this.TimeoutToken)).WithCancellation(this.TimeoutToken);
        Assert.IsType<NotSupportedException>(ex.InnerException);
        this.Logger.WriteLine(ex.ToString());
        ex = await Assert.ThrowsAsync<JsonSerializationException>(() => this.clientRpc.InvokeWithParameterObjectAsync("test", new { something = 3 })).WithCancellation(this.TimeoutToken);
        Assert.IsType<NotSupportedException>(ex.InnerException);

        Assert.Equal(0, this.messageHandler.WrittenMessages.Count);
    }

    [Fact]
    public async Task NotificationsAreSentWithNullId()
    {
        await this.clientRpc.NotifyAsync("method");
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Null, request["id"]?.Type);
    }
}
