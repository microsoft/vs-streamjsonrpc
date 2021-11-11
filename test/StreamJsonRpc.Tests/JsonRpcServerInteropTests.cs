// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Verifies the <see cref="JsonRpc"/> class's functionality as a JSON-RPC 2.0 *server* (i.e. the one receiving requests, and sending results)
/// against various client messages.
/// </summary>
public class JsonRpcServerInteropTests : InteropTestBase
{
    private readonly Server server;

    public JsonRpcServerInteropTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();
    }

    [Fact]
    public async Task ServerAcceptsNumberForMessageId()
    {
        this.InitializeServer();
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoInt",
            @params = new[] { 5 },
            id = 1,
        });

        Assert.Equal(5, (int)response.result);
        Assert.Equal(1, (int)response.id);
    }

    [Fact]
    public async Task ServerAcceptsStringForMessageId()
    {
        this.InitializeServer();
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoInt",
            @params = new[] { 5 },
            id = "abc",
        });

        Assert.Equal(5, (int)response.result);
        Assert.Equal("abc", (string)response.id);
    }

    [Fact]
    public async Task ServerAcceptsNumberForProgressId()
    {
        this.InitializeServer();
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = nameof(Server.EchoSuccessWithProgressParam),
            @params = new[] { 5 },
            id = "abc",
        });

        // If the response is returned without error it means the server succeded on using the token to create the JsonProgress instance
        Assert.Equal("Success!", (string)response.result);
    }

    [Fact]
    public async Task ServerAcceptsStringForProgressId()
    {
        this.InitializeServer();
        dynamic response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = nameof(Server.EchoSuccessWithProgressParam),
            @params = new[] { "Token" },
            id = "abc",
        });

        // If the response is returned without error it means the server succeded on using the token to create the JsonProgress instance
        Assert.Equal("Success!", (string)response.result);
    }

    /// <summary>
    /// The <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-3-17/#progress">LSP spec</see>
    /// requires that <see cref="IProgress{T}"/> notifications be sent using named arguments.
    /// This test asserts that that happens.
    /// </summary>
    [Theory, PairwiseData]
    public async Task ServerEmitsProgressNotificationsWithArgStyles(bool namedArgs)
    {
        this.InitializeServer(new JsonRpcTargetOptions { ClientRequiresNamedArguments = namedArgs });
        this.Send(new
        {
            jsonrpc = "2.0",
            method = nameof(Server.SendProgressNotificationParam),
            @params = new[] { 5 },
            id = "abc",
        });

        JToken notification = await this.ReceiveAsync();
        JToken result = await this.ReceiveAsync();

        // Assert that the server emitting no error, to start with.
        Assert.Equal(JTokenType.Null, result["result"]?.Type);

        this.Logger.WriteLine("Notification:\n{0}", notification);
        Assert.Equal("$/progress", notification.Value<string>("method"));

        // Assert that the progress notification came using the right kind of args.
        if (namedArgs)
        {
            JObject paramsObject = Assert.IsType<JObject>(notification["params"]);
            Assert.Equal(5, paramsObject.Value<int>("token"));
            Assert.Equal(8, paramsObject.Value<int>("value"));
        }
        else
        {
            JArray paramsArray = Assert.IsType<JArray>(notification["params"]);
            Assert.Equal(5, paramsArray[0].Value<int>());
            Assert.Equal(8, paramsArray[1].Value<int>());
        }
    }

    [Theory, PairwiseData]
    public async Task ServerEmitsProgressNotificationsWithNamedArguments_WithNamedArgumentsInput_WithSingleObjectParameterDeserialization(bool useSingleObjectParameterDeserialization)
    {
        this.InitializeServer(new JsonRpcTargetOptions { ClientRequiresNamedArguments = true, UseSingleObjectParameterDeserialization = useSingleObjectParameterDeserialization });

        var methodToSend = useSingleObjectParameterDeserialization ? nameof(Server.SendProgressNotificationSingleObject) : nameof(Server.SendProgressNotificationMultipleParam);
        this.Send(new
        {
            jsonrpc = "2.0",
            method = methodToSend,
            @params = new { firstParam = 1, progress = 5 },
            id = "abc",
        });

        JToken notification = await this.ReceiveAsync();
        JToken result = await this.ReceiveAsync();

        // Assert that the server emitting no error, to start with.
        Assert.Equal(JTokenType.Null, result["result"]?.Type);

        this.Logger.WriteLine("Notification:\n{0}", notification);
        Assert.Equal("$/progress", notification.Value<string>("method"));

        // Assert that the progress notification came using named args in all combinations.
        JObject paramsObject = Assert.IsType<JObject>(notification["params"]);
        Assert.Equal(5, paramsObject.Value<int>("token"));
        Assert.Equal(useSingleObjectParameterDeserialization ? 6 : 7, paramsObject.Value<int>("value"));
    }

    [Fact]
    public async Task ServerAlwaysReturnsResultEvenIfNull()
    {
        this.InitializeServer();
        var response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoString",
            @params = new object?[] { null },
            id = 1,
        });

        // Assert that result is specified, but that its value is null.
        Assert.NotNull(response["result"]);
        Assert.Null(response.Value<string>("result"));
    }

    private JsonRpc InitializeServer(JsonRpcTargetOptions? targetOptions = null)
    {
        var serverRpc = new JsonRpc(this.messageHandler);
        serverRpc.AddLocalRpcTarget(this.server, targetOptions);
        serverRpc.StartListening();
        return serverRpc;
    }

#pragma warning disable CA1801 // Review unused parameters
    private class Server
    {
        public int EchoInt(int value) => value;

        public string EchoString(string value) => value;

        public string EchoSuccessWithProgressParam(IProgress<int> progress) => "Success!";

        public void SendProgressNotificationParam(IProgress<int> progress) => progress.Report(8);

        public void SendProgressNotificationMultipleParam(int firstParam, IProgress<int> progress) => progress.Report(7);

        public void SendProgressNotificationSingleObject(InputParam input) => input.Progress?.Report(6);

        internal class InputParam
        {
            [JsonProperty("firstParam")]
            public int FirstParam { get; set; }

            [JsonProperty("progress")]
            public IProgress<int>? Progress { get; set; }
        }
    }
}
