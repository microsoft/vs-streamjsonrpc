// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

/// <summary>
/// <see cref="JsonRpc"/> tests that don't exercise the <see cref="IJsonRpcMessageHandler"/> or <see cref="IJsonRpcMessageFormatter"/>.
/// </summary>
public class JsonRpcSimpleTests
{
    [Fact]
    public void DisplayName_Default()
    {
        JsonRpc jsonRpc = new JsonRpc(Stream.Null);
        Assert.Null(jsonRpc.DisplayName);
    }

    [Fact]
    public void DisplayName_Settable()
    {
        JsonRpc jsonRpc = new(Stream.Null)
        {
            DisplayName = "Test name",
        };

        Assert.Equal("Test name", jsonRpc.DisplayName);
    }
}
