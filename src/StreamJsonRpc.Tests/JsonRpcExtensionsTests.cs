// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcExtensionsTests : TestBase
{
    public JsonRpcExtensionsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public async Task AsAsyncEnumerable()
    {
        var collection = new int[] { 1, 2, 3 };
        int count = 0;
        await foreach (int item in collection.AsAsyncEnumerable())
        {
            Assert.Equal(++count, item);
        }

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task AsAsyncEnumerable_Settings()
    {
        var collection = new int[] { 1, 2, 3 };
        int count = 0;
        await foreach (int item in collection.AsAsyncEnumerable(new JsonRpcEnumerableSettings { MinBatchSize = 3 }))
        {
            Assert.Equal(++count, item);
        }

        Assert.Equal(3, count);
    }
}
