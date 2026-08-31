// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;
using MessagePack.Resolvers;

public class AsyncEnumerableMessagePackTests : AsyncEnumerableTests
{
    public AsyncEnumerableMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public async Task GetIAsyncEnumerableAsReturnType_WithTypelessObjectResolver()
    {
        var options = MessagePackSerializerOptions.Standard.WithResolver(TypelessObjectResolver.Instance);
        ((MessagePackFormatter)this.serverMessageFormatter).SetMessagePackSerializerOptions(options);
        ((MessagePackFormatter)this.clientMessageFormatter).SetMessagePackSerializerOptions(options);

        int realizedValuesCount = 0;
        await foreach (int number in this.clientProxy.Value.GetNumbersAsync(this.TimeoutToken))
        {
            realizedValuesCount++;
        }

        Assert.Equal(Server.ValuesReturnedByEnumerables, realizedValuesCount);
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new MessagePackFormatter();
        this.clientMessageFormatter = new MessagePackFormatter();
    }
}
