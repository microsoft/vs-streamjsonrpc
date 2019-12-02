// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcExtensionsTests : TestBase
{
    private static readonly IReadOnlyList<int> SmallList = new int[] { 1, 2, 3 };

    public JsonRpcExtensionsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public async Task AsAsyncEnumerable()
    {
        await AssertExpectedValues(SmallList.AsAsyncEnumerable());
    }

    [Fact]
    public async Task AsAsyncEnumerable_Settings()
    {
        await AssertExpectedValues(SmallList.AsAsyncEnumerable(new JsonRpcEnumerableSettings { MinBatchSize = 3 }));
    }

    [Fact]
    public async Task AsAsyncEnumerable_Cancellation()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        var cts = new CancellationTokenSource();

        var enumerator = asyncEnum.GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(SmallList[0], enumerator.Current);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task WithPrefetchAsync_ZeroCount()
    {
        IAsyncEnumerable<int> asyncEnum = SmallList.AsAsyncEnumerable();
        Assert.Same(asyncEnum, await asyncEnum.WithPrefetchAsync(0, this.TimeoutToken));
    }

    [Fact]
    public async Task WithPrefetchAsync_NegativeCount()
    {
        IAsyncEnumerable<int> asyncEnum = SmallList.AsAsyncEnumerable();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("count", async () => await asyncEnum.WithPrefetchAsync(-1));
    }

    [Fact]
    public async Task WithPrefetchAsync_Twice()
    {
        var prefetchEnum = await SmallList.AsAsyncEnumerable().WithPrefetchAsync(2, this.TimeoutToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await prefetchEnum.WithPrefetchAsync(2, this.TimeoutToken));
    }

    [Fact]
    public async Task WithPrefetchAsync()
    {
        await AssertExpectedValues(await SmallList.AsAsyncEnumerable().WithPrefetchAsync(2, this.TimeoutToken));
    }

    [Fact]
    public void WithJsonRpcSettings_NullInput()
    {
        Assert.Throws<ArgumentNullException>("enumerable", () => JsonRpcExtensions.WithJsonRpcSettings<int>(null!, null));
    }

    [Fact]
    public void WithJsonRpcSettings_NullSettings()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        Assert.Same(asyncEnum, asyncEnum.WithJsonRpcSettings(null));
    }

    [Fact]
    public void WithJsonRpcSettings_GetAsyncEnumerator_Twice()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        var decorated = asyncEnum.WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = 3 });
        decorated.GetAsyncEnumerator();
        Assert.Throws<InvalidOperationException>(() => decorated.GetAsyncEnumerator());
    }

    [Fact]
    public async Task WithJsonRpcSettings_GetAsyncEnumerator_Prefetch()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        var decorated = asyncEnum.WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = 3 });
        decorated.GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await decorated.WithPrefetchAsync(2));
    }

    [Fact]
    public async Task WithPrefetch_GetAsyncEnumerator_Twice()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        var decorated = await asyncEnum.WithPrefetchAsync(2, this.TimeoutToken);
        decorated.GetAsyncEnumerator();
        Assert.Throws<InvalidOperationException>(() => decorated.GetAsyncEnumerator());
    }

    [Fact]
    public async Task WithPrefetch_GetAsyncEnumerator_WithPrefetch()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        var decorated = await asyncEnum.WithPrefetchAsync(2, this.TimeoutToken);
        decorated.GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await decorated.WithPrefetchAsync(2));
    }

    [Fact]
    public void WithJsonRpcSettings_ModifySettings()
    {
        var asyncEnum = SmallList.AsAsyncEnumerable();
        var decorated = asyncEnum.WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = 3 });
        Assert.NotSame(asyncEnum, decorated);

        // This shouldn't need to rewrap the underlying enumerator or double-decorate.
        // Rather, it should just modify the existing decorator.
        Assert.Same(decorated, decorated.WithJsonRpcSettings(new JsonRpcEnumerableSettings { MaxReadAhead = 5 }));
    }

    private static async Task AssertExpectedValues(IAsyncEnumerable<int> actual)
    {
        int count = 0;
        await foreach (int item in actual)
        {
            Assert.Equal(++count, item);
        }

        Assert.Equal(3, count);
    }
}
