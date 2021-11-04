// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc;
using Xunit;

public class JsonRpcTargetOptionsTests
{
    [Fact]
    public void CopyConstructor()
    {
        JsonRpcTargetOptions options = new();
        options.AllowNonPublicInvocation = !options.AllowNonPublicInvocation;
        options.ClientRequiresNamedArguments = !options.ClientRequiresNamedArguments;
        options.DisposeOnDisconnect = !options.DisposeOnDisconnect;
        options.EventNameTransform = s => s;
        options.MethodNameTransform = s => s;
        options.NotifyClientOfEvents = !options.NotifyClientOfEvents;
        options.UseSingleObjectParameterDeserialization = !options.UseSingleObjectParameterDeserialization;

        JsonRpcTargetOptions copy = new(options);
        Assert.Equal(options.AllowNonPublicInvocation, copy.AllowNonPublicInvocation);
        Assert.Equal(options.ClientRequiresNamedArguments, copy.ClientRequiresNamedArguments);
        Assert.Equal(options.DisposeOnDisconnect, copy.DisposeOnDisconnect);
        Assert.Equal(options.EventNameTransform, copy.EventNameTransform);
        Assert.Equal(options.MethodNameTransform, copy.MethodNameTransform);
        Assert.Equal(options.NotifyClientOfEvents, copy.NotifyClientOfEvents);
        Assert.Equal(options.UseSingleObjectParameterDeserialization, copy.UseSingleObjectParameterDeserialization);
    }
}
