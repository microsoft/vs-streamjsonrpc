// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[Obsolete(ObsoleteFormatter)]
public class JsonRpcClient20InteropJsonMessageFormatterTests : JsonRpcClient20InteropTests
{
    public JsonRpcClient20InteropJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }

    [Fact]
    public async Task NotifyWithParameterPassedAsObjectAsync_ThrowsExceptions()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyWithParameterObjectAsync("test", new int[] { 1, 2 }));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    /// <summary>
    /// Some lesser JSON-RPC servers may convert the request ID from the JSON number that we sent to a string.
    /// Reproduce that to verify that our client functionality is resilient enough to withstand that bad behavior.
    /// </summary>
    [Fact]
    public async Task ServerReturnsOurRequestIdAsString()
    {
        var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test", cancellationToken: this.TimeoutToken);
        dynamic request = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = request.id.ToString(),
            result = "pass",
        });

        string result = await invokeTask.WithCancellation(this.TimeoutToken);
        Assert.Equal("pass", result);
    }

    [Fact]
    public async Task SerializeWithNoWhitespace()
    {
        _ = this.clientRpc.NotifyAsync("test");
        JToken message = await this.messageHandler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        string json = message.ToString(Formatting.None);
        this.Logger.WriteLine(json);
        Assert.Equal(@"{""jsonrpc"":""2.0"",""method"":""test"",""params"":[]}", json);
    }

    [Fact]
    public async Task SerializeWithPrettyFormatting()
    {
        _ = this.clientRpc.NotifyAsync("test");
        JToken message = await this.messageHandler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        string json = message.ToString();
        this.Logger.WriteLine(json);
        string expected = "{\n  \"jsonrpc\": \"2.0\",\n  \"method\": \"test\",\n  \"params\": []\n}"
            .Replace("\n", Environment.NewLine);
        Assert.Equal(expected, json);
    }
}
