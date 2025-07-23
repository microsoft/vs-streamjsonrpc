// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class JsonRpcProxyOptionsTests
{
    [Fact]
    public void DefaultIsFrozen()
    {
        Assert.Throws<InvalidOperationException>(() => JsonRpcProxyOptions.Default.MethodNameTransform = s => s);
        Assert.Throws<InvalidOperationException>(() => JsonRpcProxyOptions.Default.EventNameTransform = s => s);
        Assert.Throws<InvalidOperationException>(() => JsonRpcProxyOptions.Default.ServerRequiresNamedArguments = true);
    }

    [Fact]
    public void NewInstanceIsNotFrozen()
    {
        JsonRpcProxyOptions options = new()
        {
            MethodNameTransform = s => s,
            EventNameTransform = s => s,
            ServerRequiresNamedArguments = true,
        };
    }
}
