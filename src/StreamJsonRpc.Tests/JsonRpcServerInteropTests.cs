// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcServerInteropTests : InteropTestBase
{
    private readonly Server server;
    private readonly JsonRpc serverRpc;

    public JsonRpcServerInteropTests(ITestOutputHelper logger)
        : base(logger, serverTest: true)
    {
        this.server = new Server();
        this.serverRpc = new JsonRpc(this.messageHandler, this.server);
        this.serverRpc.StartListening();
    }

    [Fact]
    public async Task ServerAcceptsNumberForMessageId()
    {
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
    public async Task ServerAlwaysReturnsResultEvenIfNull()
    {
        var response = await this.RequestAsync(new
        {
            jsonrpc = "2.0",
            method = "EchoString",
            @params = new object[] { null },
            id = 1,
        });

        // Assert that result is specified, but that its value is null.
        Assert.NotNull(response["result"]);
        Assert.Null(response.Value<string>("result"));
    }

    private class Server
    {
        public int EchoInt(int value) => value;

        public string EchoString(string value) => value;
    }
}
