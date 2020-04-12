// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using StreamJsonRpc;
using Xunit;

public class JsonRpcEnumerableSettingsTests
{
    private JsonRpcEnumerableSettings settings = new JsonRpcEnumerableSettings();

    [Fact]
    public void MinBatchSize_Default()
    {
        Assert.Equal(1, this.settings.MinBatchSize);
    }

    [Fact]
    public void MaxReadAhead_Default()
    {
        Assert.Equal(0, this.settings.MaxReadAhead);
    }

    [Fact]
    public void Prefetch_Default()
    {
        Assert.Equal(0, this.settings.Prefetch);
    }

    [Fact]
    public void MinBatchSize_AcceptsOnlyPositiveIntegers()
    {
        this.settings.MinBatchSize = 1;
        this.settings.MinBatchSize = 10;
        Assert.Throws<ArgumentOutOfRangeException>(() => this.settings.MinBatchSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => this.settings.MinBatchSize = -1);
    }

    [Fact]
    public void Prefetch_AcceptsOnlyNonNegativeIntegers()
    {
        this.settings.Prefetch = 10;
        this.settings.Prefetch = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => this.settings.Prefetch = -1);
    }

    [Fact]
    public void MaxReadAhead_AcceptsOnlyNonNegativeIntegers()
    {
        this.settings.MaxReadAhead = 10;
        this.settings.MaxReadAhead = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => this.settings.MaxReadAhead = -1);
    }
}
