// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcMessagePackLengthTests : JsonRpcTests
{
    public JsonRpcMessagePackLengthTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public async Task CanPassAndCallPrivateMethodsObjects()
    {
        var result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new Foo { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new MessagePackFormatter();
        this.clientMessageFormatter = new MessagePackFormatter();

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, this.serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
    }
}
