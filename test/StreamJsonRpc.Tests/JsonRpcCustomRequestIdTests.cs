// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcCustomRequestIdTests : InteropTestBase
{
    private JsonRpcWithStringIds clientRpc;

    public JsonRpcCustomRequestIdTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.clientRpc = new JsonRpcWithStringIds(this.messageHandler)
        {
            TraceSource =
            {
                Switch = { Level = SourceLevels.Verbose },
                Listeners = { new XunitTraceListener(logger) },
            },
        };
        this.clientRpc.StartListening();
    }

    [Fact]
    public async Task ClientSendsUniqueIdAsString()
    {
        var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test", cancellationToken: this.TimeoutToken);
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.String, request["id"].Type);
        string idAsString = request.Value<string>("id");
        Assert.StartsWith("prefix-", idAsString);
        this.Send(new
        {
            jsonrpc = "2.0",
            id = idAsString,
            result = "pass",
        });

        string result = await invokeTask.WithCancellation(this.TimeoutToken);
        Assert.Equal("pass", result);
    }

    private class JsonRpcWithStringIds : JsonRpc
    {
        public JsonRpcWithStringIds(IJsonRpcMessageHandler handler)
            : base(handler)
        {
        }

        protected override RequestId CreateNewRequestId()
        {
            return new RequestId("prefix-" + base.CreateNewRequestId().Number);
        }
    }
}
