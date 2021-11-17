// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class ExceptionSettingsTests : TestBase
{
    public ExceptionSettingsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void RecursionLimit()
    {
        Assert.Equal(int.MaxValue, ExceptionSettings.TrustedData.RecursionLimit);
        Assert.Equal(50, ExceptionSettings.UntrustedData.RecursionLimit);
    }

    [Fact]
    public void CanDeserialize()
    {
        Assert.True(ExceptionSettings.TrustedData.CanDeserialize(typeof(AppDomain)));
        Assert.False(ExceptionSettings.UntrustedData.CanDeserialize(typeof(AppDomain)));

        Assert.True((ExceptionSettings.TrustedData with { RecursionLimit = 3 }).CanDeserialize(typeof(AppDomain)));
        Assert.False((ExceptionSettings.UntrustedData with { RecursionLimit = 3 }).CanDeserialize(typeof(AppDomain)));
    }
}
