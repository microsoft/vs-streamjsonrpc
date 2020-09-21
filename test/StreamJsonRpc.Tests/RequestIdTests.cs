// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class RequestIdTests : TestBase
{
    public RequestIdTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void StringValue()
    {
        Assert.Equal("s", new RequestId("s").String);
        Assert.Null(new RequestId(null).String);
        Assert.Null(new RequestId(3).String);
    }

    [Fact]
    public void NumberValue()
    {
        Assert.Equal(3, new RequestId(3).Number);
        Assert.Null(new RequestId(null).Number);
        Assert.Null(new RequestId("3").Number);
    }

    [Fact]
    public void IsNull()
    {
        Assert.True(RequestId.Null.IsNull);
        Assert.False(RequestId.NotSpecified.IsNull);
        Assert.False(default(RequestId).IsNull);
        Assert.True(new RequestId(null).IsNull);
        Assert.False(new RequestId("string").IsNull);
        Assert.False(new RequestId(1).IsNull);
    }

    [Fact]
    public void IsEmpty()
    {
        Assert.True(RequestId.NotSpecified.IsEmpty);
        Assert.True(default(RequestId).IsEmpty);
        Assert.False(RequestId.Null.IsEmpty);
        Assert.False(new RequestId("string").IsEmpty);
        Assert.False(new RequestId(null).IsEmpty);
        Assert.False(new RequestId(1).IsEmpty);
    }

    [Fact]
    public void Equals_Method()
    {
        Assert.True(RequestId.NotSpecified.Equals(RequestId.NotSpecified));
        Assert.True(RequestId.Null.Equals(RequestId.Null));
        Assert.False(RequestId.NotSpecified.Equals(RequestId.Null));
        Assert.False(new RequestId("string").Equals(RequestId.NotSpecified));
        Assert.False(new RequestId("string").Equals(RequestId.Null));
        Assert.False(new RequestId(1).Equals(RequestId.NotSpecified));
        Assert.False(new RequestId(1).Equals(RequestId.Null));
    }

    [Fact]
    public void ToString_Method()
    {
        Assert.Equal("s", new RequestId("s").ToString());
        Assert.Equal("1", new RequestId(1).ToString());
        Assert.Equal("(null)", RequestId.Null.ToString());
        Assert.Equal("(not specified)", RequestId.NotSpecified.ToString());
    }
}
