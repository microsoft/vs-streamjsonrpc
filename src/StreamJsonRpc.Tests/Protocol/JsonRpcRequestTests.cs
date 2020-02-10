// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using StreamJsonRpc.Protocol;
using Xunit;

public class JsonRpcRequestTests
{
    private static readonly IReadOnlyList<object> ArgumentsAsList = new List<object> { 4, 6, 8 };
    private static readonly IReadOnlyList<object> ArgumentsAsArray = new object[] { 4, 6, 8 };
    private static readonly object ArgumentsAsObject = new Dictionary<string, object> { { "Foo", 4 }, { "bar", 6 } };

    [Fact]
    public void ArgumentsCount_WithNamedArguments()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsObject,
        };
        Assert.Equal(2, request.ArgumentCount);
    }

    [Fact]
    public void ArgumentsCount_WithList()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsList,
        };
        Assert.Equal(3, request.ArgumentCount);
    }

    [Fact]
    public void ArgumentsCount_WithArray()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsArray,
        };
        Assert.Equal(3, request.ArgumentCount);
    }

    [Fact]
    public void ArgumentsList()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsList,
        };
        Assert.Same(request.Arguments, request.ArgumentsList);
        Assert.Same(ArgumentsAsList, request.ArgumentsList);
    }

#pragma warning disable CS0618 // Type or member is obsolete
    [Fact]
    public void ArgumentsArray_WithArray()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsArray,
        };
        Assert.Same(request.Arguments, request.ArgumentsArray);
        Assert.Same(ArgumentsAsArray, request.ArgumentsArray);
    }

    [Fact]
    public void ArgumentsArray_WithList()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsList,
        };
        Assert.Null(request.ArgumentsArray);
    }
#pragma warning restore CS0618 // Type or member is obsolete
}
