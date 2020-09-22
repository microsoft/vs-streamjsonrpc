// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class RpcArgumentDeserializationExceptionTests : TestBase
{
    private const string SomeMessage = "test message";
    private static readonly Exception SomeInnerException = new Exception();

    public RpcArgumentDeserializationExceptionTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void Ctor_Message()
    {
        var ex = new RpcArgumentDeserializationException(SomeMessage);
        Assert.Equal(SomeMessage, ex.Message);
    }

    [Fact]
    public void Ctor_Message_Exception()
    {
        var ex = new RpcArgumentDeserializationException(SomeMessage, SomeInnerException);
        Assert.Equal(SomeMessage, ex.Message);
        Assert.Same(SomeInnerException, ex.InnerException);
    }

    [Fact]
    public void Ctor_WithDetails()
    {
        var ex = new RpcArgumentDeserializationException("argName", 67856, typeof(RpcArgumentDeserializationExceptionTests), SomeInnerException);
        Assert.Equal("argName", ex.ArgumentName);
        Assert.Equal(67856, ex.ArgumentPosition);
        Assert.Equal(typeof(RpcArgumentDeserializationExceptionTests), ex.DeserializedType);
        Assert.Same(SomeInnerException, ex.InnerException);
    }

    [Fact]
    public void Serializable_WithPosition()
    {
        var original = new RpcArgumentDeserializationException("argName", 67856, typeof(RpcArgumentDeserializationExceptionTests), SomeInnerException);
        var deserialized = BinaryFormatterRoundtrip(original);
        Assert.Equal(original.Message, deserialized.Message);
        Assert.Equal(original.ArgumentName, deserialized.ArgumentName);
        Assert.Equal(original.ArgumentPosition, deserialized.ArgumentPosition);
    }

    [Fact]
    public void Serializable_WithNoPosition()
    {
        var original = new RpcArgumentDeserializationException("argName", argumentPosition: null, typeof(RpcArgumentDeserializationExceptionTests), SomeInnerException);
        var deserialized = BinaryFormatterRoundtrip(original);
        Assert.Equal(original.Message, deserialized.Message);
        Assert.Equal(original.ArgumentName, deserialized.ArgumentName);
        Assert.Equal(original.ArgumentPosition, deserialized.ArgumentPosition);
    }
}
