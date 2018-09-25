// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
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

    [Fact]
    public async Task ExceptionControllingErrorData()
    {
        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.ThrowRemoteInvocationException)));

        IDictionary<object, object> data = (IDictionary<object, object>)exception.ErrorData;
        object myCustomData = data["myCustomData"];
        string actual = (string)myCustomData;
        Assert.Equal("hi", actual);
    }

    [Fact]
    public async Task CanPassExceptionFromServer()
    {
#pragma warning disable SA1139 // Use literal suffix notation instead of casting
        const int COR_E_UNAUTHORIZEDACCESS = unchecked((int)0x80070005);
#pragma warning restore SA1139 // Use literal suffix notation instead of casting
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.Equal((int)JsonRpcErrorCode.InvocationError, exception.ErrorCode);
        var errorData = (CommonErrorData)exception.ErrorData;
        Assert.NotNull(errorData.StackTrace);
        Assert.StrictEqual(COR_E_UNAUTHORIZEDACCESS, errorData.HResult);
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new MessagePackFormatter();
        this.clientMessageFormatter = new MessagePackFormatter();

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, this.serverMessageFormatter);
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
    }
}
