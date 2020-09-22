// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class RemoteInvocationExceptionTests : TestBase
{
    private const string SomeMessage = "test message";
    private static readonly Exception SomeInnerException = new Exception();

    public RemoteInvocationExceptionTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void Ctor_Message_Code_Data()
    {
        var data = new CommonErrorData();
        var ex = new RemoteInvocationException(SomeMessage, 123, data);
        Assert.Equal(SomeMessage, ex.Message);
        Assert.Equal(123, ex.ErrorCode);
        Assert.Same(data, ex.ErrorData);
        Assert.Null(ex.DeserializedErrorData);
    }

    [Fact]
    public void Ctor_Message_Code_Data_DeserializedData()
    {
        var data = new CommonErrorData();
        var deserializedData = new CommonErrorData();
        var ex = new RemoteInvocationException(SomeMessage, 123, data, deserializedData);
        Assert.Equal(SomeMessage, ex.Message);
        Assert.Equal(123, ex.ErrorCode);
        Assert.Same(data, ex.ErrorData);
        Assert.Same(deserializedData, ex.DeserializedErrorData);
    }

    [Fact]
    public void Serializable()
    {
        var data = new CommonErrorData();
        var deserializedData = new CommonErrorData();
        var original = new RemoteInvocationException(SomeMessage, 123, data, deserializedData);
        var deserialized = BinaryFormatterRoundtrip(original);
        Assert.Equal(original.Message, deserialized.Message);
        Assert.Equal(original.ErrorCode, deserialized.ErrorCode);
        Assert.Null(deserialized.ErrorData);
        Assert.Null(deserialized.DeserializedErrorData);
    }
}
