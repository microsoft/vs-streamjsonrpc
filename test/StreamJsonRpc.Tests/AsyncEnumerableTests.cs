// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using MessagePack;
using MessagePack.Formatters;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json;
using PolyType;
using NBMP = Nerdbank.MessagePack;

public abstract partial class AsyncEnumerableTests : TestBase, IAsyncLifetime
{
    protected readonly Server server = new();
    protected readonly Client client = new();

    protected JsonRpc serverRpc;
    protected IJsonRpcMessageFormatter serverMessageFormatter;

    protected Lazy<IServer> clientProxy;
    protected Lazy<IClient> serverProxy;
    protected JsonRpc clientRpc;
    protected IJsonRpcMessageFormatter clientMessageFormatter;

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
    protected AsyncEnumerableTests(ITestOutputHelper logger)
        : base(logger)
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
    {
    }

    /// <summary>
    /// This interface should NOT be implemented by <see cref="Server"/>,
    /// since the server implements the methods on this interface with a return type of Task{T}
    /// but we want the client proxy to NOT be that.
    /// </summary>
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IServer2
    {
        IAsyncEnumerable<int> WaitTillCanceledBeforeReturningAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersParameterizedAsync(int batchSize, int readAhead, int prefetch, int totalCount, bool endWithException, CancellationToken cancellationToken);
    }

    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IServer
    {
        IAsyncEnumerable<int> GetValuesFromEnumeratedSourceAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersInBatchesAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersWithReadAheadAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersThatWerePassedInAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersNoCancellationAsync();

        IAsyncEnumerable<int> WaitTillCanceledBeforeFirstItemAsync(CancellationToken cancellationToken);

        Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeReturningAsync(CancellationToken cancellationToken);

        Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeFirstItemWithPrefetchAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAsync(CancellationToken cancellationToken);

        Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAndTaskWrapperAsync(CancellationToken cancellationToken);

        Task<CompoundEnumerableResult> GetNumbersAndMetadataAsync(CancellationToken cancellationToken);

        Task PassInNumbersAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);

        Task PassInNumbersAndIgnoreAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);

        Task PassInNumbersOnlyStartEnumerationAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);

        IAsyncEnumerable<string> CallbackClientAndYieldOneValueAsync(CancellationToken cancellationToken);
    }

    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IClient
    {
        Task DoSomethingAsync(CancellationToken cancellationToken);
    }

    public ValueTask InitializeAsync()
    {
        Tuple<Nerdbank.FullDuplexStream, Nerdbank.FullDuplexStream> streams = Nerdbank.FullDuplexStream.CreateStreams();

        this.InitializeFormattersAndHandlers();

        var serverHandler = new LengthHeaderMessageHandler(streams.Item1.UsePipe(), this.serverMessageFormatter);
        var clientHandler = new LengthHeaderMessageHandler(streams.Item2.UsePipe(), this.clientMessageFormatter);

        this.serverRpc = new JsonRpc(serverHandler, this.server);
        this.clientRpc = new JsonRpc(clientHandler, this.client);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        this.clientProxy = new Lazy<IServer>(() => this.clientRpc.Attach<IServer>());
        this.serverProxy = new Lazy<IClient>(() => this.serverRpc.Attach<IClient>());

        return default;
    }

    public ValueTask DisposeAsync()
    {
        this.serverRpc.Dispose();
        this.clientRpc.Dispose();

        if (this.serverRpc.Completion.IsFaulted)
        {
            this.Logger.WriteLine("Server faulted with: " + this.serverRpc.Completion.Exception);
        }

        return default;
    }

    [Theory]
    [PairwiseData]
    public async Task GetIAsyncEnumerableAsReturnType(bool useProxy)
    {
        int realizedValuesCount = 0;
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.GetNumbersAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersAsync), cancellationToken: this.TimeoutToken);
        await foreach (int number in enumerable)
        {
            realizedValuesCount++;
            this.Logger.WriteLine(number.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(Server.ValuesReturnedByEnumerables, realizedValuesCount);
    }

    [Theory]
    [PairwiseData]
    public async Task GetIAsyncEnumerableAsReturnTypeAndParameter(bool useProxy)
    {
        IAsyncEnumerable<int>? numbers = Enumerable.Range(1, Server.ValuesReturnedByEnumerables).AsAsyncEnumerable();

        int realizedValuesCount = 0;
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.GetNumbersThatWerePassedInAsync(numbers, this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersThatWerePassedInAsync), new object[] { numbers }, this.TimeoutToken);
        await foreach (int number in enumerable)
        {
            realizedValuesCount++;
            this.Logger.WriteLine(number.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(Server.ValuesReturnedByEnumerables, realizedValuesCount);
    }

    [Fact]
    public async Task GetIAsyncEnumerableAsReturnType_WithProxy_NoCancellation()
    {
        int realizedValuesCount = 0;
        IAsyncEnumerable<int> enumerable = this.clientProxy.Value.GetNumbersNoCancellationAsync();
        await foreach (int number in enumerable)
        {
            realizedValuesCount++;
            this.Logger.WriteLine(number.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(Server.ValuesReturnedByEnumerables, realizedValuesCount);
    }

    [Theory]
    [PairwiseData]
    public async Task GetIAsyncEnumerableAsMemberWithinReturnType(bool useProxy)
    {
        int realizedValuesCount = 0;
        CompoundEnumerableResult result = useProxy
            ? await this.clientProxy.Value.GetNumbersAndMetadataAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<CompoundEnumerableResult>(nameof(Server.GetNumbersAndMetadataAsync), cancellationToken: this.TimeoutToken);
        Assert.Equal("Hello!", result.Message);
        Assert.NotNull(result.Enumeration);
        await foreach (int number in result.Enumeration!)
        {
            realizedValuesCount++;
            this.Logger.WriteLine(number.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(Server.ValuesReturnedByEnumerables, realizedValuesCount);
    }

    [Theory]
    [PairwiseData]
    public async Task GetIAsyncEnumerableAsReturnType_MinBatchSize(bool useProxy)
    {
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.GetNumbersInBatchesAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersInBatchesAsync), cancellationToken: this.TimeoutToken);
        var enumerator = enumerable.GetAsyncEnumerator(this.TimeoutToken);

        for (int i = 0; i < Server.ValuesReturnedByEnumerables; i++)
        {
            // Assert that the server is always in a state of having produced values to fill a batch.
            Assert.True(this.server.ActuallyGeneratedValueCount % Server.MinBatchSize == 0 || this.server.ActuallyGeneratedValueCount == Server.ValuesReturnedByEnumerables);

            // Assert that the ValueTask completes synchronously within a batch.
            if (i % Server.MinBatchSize == 0)
            {
                // A new batch should be requested here. Allow for an async completion.
                // But avoid asserting that it completed asynchronously or else in certain race conditions the test will fail
                // simply because the async portion happened before the test could check the completion flag.
                Assert.True(await enumerator.MoveNextAsync());
            }
            else
            {
                // Within a batch, the MoveNextAsync call should absolutely complete synchronously.
                ValueTask<bool> valueTask = enumerator.MoveNextAsync();
                Assert.True(valueTask.IsCompleted);
                Assert.True(await valueTask);
            }

            int number = enumerator.Current;
            this.Logger.WriteLine(number.ToString(CultureInfo.InvariantCulture));
        }

        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task GetIAsyncEnumerableAsReturnType_MaxReadAhead()
    {
        IAsyncEnumerable<int> enumerable = this.clientProxy.Value.GetNumbersWithReadAheadAsync(this.TimeoutToken);

        async Task ExpectReadAhead(int expected)
        {
            while (true)
            {
                Task valueGenerated = this.server.ValueGenerated.WaitAsync(this.TimeoutToken);
                if (this.server.ActuallyGeneratedValueCount >= expected)
                {
                    break;
                }

                await valueGenerated;
            }
        }

        // Without reading the first value, the server should be getting ahead.
        await ExpectReadAhead(Server.MaxReadAhead);

        // Read the first value. That should cause the client to get everything the server had, leading to the server refilling the read-ahead.
        var enumerator = enumerable.GetAsyncEnumerator(this.TimeoutToken);
        Assert.True(await enumerator.MoveNextAsync());
        await ExpectReadAhead(Server.ValuesReturnedByEnumerables);

        async Task Consume(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await enumerator.MoveNextAsync();
            }
        }

        // Consume the rest of the batch. Confirm the server hasn't produced any more.
        await Consume(Server.MaxReadAhead - 1);
        Assert.Equal(Server.ValuesReturnedByEnumerables, this.server.ActuallyGeneratedValueCount);

        // Consume one more, which should consume the batch.
        Assert.True(await enumerator.MoveNextAsync());
        await Consume(3);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Theory]
    [PairwiseData]
    public async Task PassInIAsyncEnumerableAsArgument(bool useProxy)
    {
#pragma warning disable CS8425 // Async-iterator member has one or more parameters of type 'CancellationToken' but none of them is decorated with the 'EnumeratorCancellation' attribute, so the cancellation token parameter from the generated 'IAsyncEnumerable<>.GetAsyncEnumerator' will be unconsumed
        async IAsyncEnumerable<int> Generator(CancellationToken cancellationToken)
#pragma warning restore CS8425 // Async-iterator member has one or more parameters of type 'CancellationToken' but none of them is decorated with the 'EnumeratorCancellation' attribute, so the cancellation token parameter from the generated 'IAsyncEnumerable<>.GetAsyncEnumerator' will be unconsumed
        {
            for (int i = 1; i <= Server.ValuesReturnedByEnumerables; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i;
            }
        }

        if (useProxy)
        {
            await this.clientProxy.Value.PassInNumbersAsync(Generator(this.TimeoutToken), this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithCancellationAsync(nameof(Server.PassInNumbersAsync), new object[] { Generator(this.TimeoutToken) }, this.TimeoutToken);
        }
    }

    [Fact]
    public void EnumerablesAndBatchSizeAreAsIntended()
    {
        // As per the comment on Server.ValuesReturnedByEnumerables, assert that the value of the test is ensured
        // by checking that an enumeration will not end at a convenient batch size boundary.
        Assert.NotEqual(0, Server.ValuesReturnedByEnumerables % Server.MinBatchSize);
    }

    [Theory]
    [PairwiseData]
    public async Task Cancellation_BeforeMoveNext(bool useProxy)
    {
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.GetNumbersAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersAsync), cancellationToken: this.TimeoutToken);

        var cts = new CancellationTokenSource();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Theory]
    [PairwiseData]
    public async Task Cancellation_AfterFirstMoveNext(bool useProxy)
    {
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.GetNumbersAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersAsync), cancellationToken: this.TimeoutToken);

        var cts = new CancellationTokenSource();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_AfterFirstMoveNext_NaturalForEach_Proxy()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
        IAsyncEnumerable<int> enumerable = this.clientProxy.Value.GetNumbersAsync(cts.Token);

        int iterations = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async delegate
        {
            await foreach (var item in enumerable)
            {
                iterations++;
                cts.Cancel();
            }
        }).WithCancellation(this.TimeoutToken);

        Assert.Equal(1, iterations);
    }

    [Fact]
    public async Task Cancellation_AfterFirstMoveNext_NaturalForEach_NoProxy()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
        var enumerable = await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersAsync), cancellationToken: cts.Token);

        int iterations = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async delegate
        {
            await foreach (var item in enumerable.WithCancellation(cts.Token))
            {
                iterations++;
                cts.Cancel();
            }
        }).WithCancellation(this.TimeoutToken);

        Assert.Equal(1, iterations);
    }

    [Theory]
    [PairwiseData]
    public async Task Cancellation_DuringLongRunningServerMoveNext(bool useProxy)
    {
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.WaitTillCanceledBeforeFirstItemAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.WaitTillCanceledBeforeFirstItemAsync), cancellationToken: this.TimeoutToken);

        var cts = new CancellationTokenSource();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();
        Assert.False(moveNextTask.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNextTask).WithCancellation(this.TimeoutToken);
    }

    [Theory]
    [PairwiseData]
    public async Task Cancellation_DuringLongRunningServerBeforeReturning(bool useProxy, [CombinatorialValues(0, 1, 2, 3)] int prefetchStrategy)
    {
        var cts = new CancellationTokenSource();
        string rpcMethodName =
            prefetchStrategy == 0 ? nameof(Server.WaitTillCanceledBeforeReturningAsync) :
            prefetchStrategy == 1 ? nameof(Server.WaitTillCanceledBeforeFirstItemWithPrefetchAsync) :
            prefetchStrategy == 2 ? nameof(Server.WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAsync) :
            prefetchStrategy == 3 ? nameof(Server.WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAndTaskWrapperAsync) :
            throw new ArgumentOutOfRangeException(nameof(prefetchStrategy));

        Task<IAsyncEnumerable<int>> enumerable = useProxy
            ? (prefetchStrategy == 0 ? this.clientProxy.Value.WaitTillCanceledBeforeReturningAsync(cts.Token) :
               prefetchStrategy == 1 ? this.clientProxy.Value.WaitTillCanceledBeforeFirstItemWithPrefetchAsync(cts.Token) :
               prefetchStrategy == 2 ? Task.FromResult(this.clientProxy.Value.WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAsync(cts.Token)) :
               prefetchStrategy == 3 ? this.clientProxy.Value.WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAndTaskWrapperAsync(cts.Token) :
               throw new ArgumentOutOfRangeException(nameof(prefetchStrategy)))
            : this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(rpcMethodName, cancellationToken: cts.Token);

        // Make sure the method has been invoked first.
        await this.server.MethodEntered.WaitAsync(this.TimeoutToken);

        // Now cancel the server method to get it to throw OCE.
        cts.Cancel();

        // Verify that it does throw OCE. Or timeout and fail the test if it doesn't.
        if (prefetchStrategy == 2 && useProxy)
        {
            // In this strategy, we just wrapped up the IAsyncEnumerable in a pre-completed task, so we won't observe cancellation until we start enumerating.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await (await enumerable).GetAsyncEnumerator(TestContext.Current.CancellationToken).MoveNextAsync()).WithCancellation(this.TimeoutToken);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerable).WithCancellation(this.TimeoutToken);
        }
    }

    [Fact]
    public async Task Cancellation_DuringLongRunningServerBeforeReturning_NonTaskReturningProxy()
    {
        var clientProxy = this.clientRpc.Attach<IServer2>();
        var cts = new CancellationTokenSource();
        IAsyncEnumerable<int> enumerable = clientProxy.WaitTillCanceledBeforeReturningAsync(cts.Token);

        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();
        Assert.False(moveNextTask.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNextTask).WithCancellation(this.TimeoutToken);
    }

    [Theory]
    [PairwiseData]
    public async Task DisposeMidEnumeration(bool useProxy)
    {
        IAsyncEnumerable<int> enumerable = useProxy
            ? this.clientProxy.Value.GetNumbersAsync(this.TimeoutToken)
            : await this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.GetNumbersAsync), cancellationToken: this.TimeoutToken);

        await foreach (var item in enumerable)
        {
            // Break out after getting the first item.
            break;
        }

        await this.server.MethodExited.WaitAsync(this.TimeoutToken);
    }

    [Fact]
    public async Task NotifyAsync_ThrowsIfAsyncEnumerableSent()
    {
        // Sending IAsyncEnumerable<T> requires the sender to store a reference to the enumerable until the receiver clears it.
        // But for a notification there's no guarantee the server handles the message and no way to get an error back,
        // so it simply should not be allowed since the risk of memory leak is too high.
        var numbers = new int[] { 1, 2, 3 }.AsAsyncEnumerable();
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyAsync(nameof(Server.PassInNumbersAsync), new object?[] { numbers }));
        this.Logger.WriteLine(ex.ToString());
        ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyAsync(nameof(Server.PassInNumbersAsync), new object?[] { new CompoundEnumerableResult { Enumeration = numbers } }));
        this.Logger.WriteLine(ex.ToString());
    }

    [Fact]
    [Trait("GC", "")]
    public async Task ArgumentEnumerable_ReleasedOnErrorResponse()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ReleasedOnErrorResponse_Helper();
        await Task.Yield(); // get off the helper's inline continuation stack.
        AssertCollectedObject(enumerable);
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("FailsOnMono", "true")]
    public async Task ArgumentEnumerable_ReleasedOnErrorInSubsequentArgumentSerialization()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ReleasedOnErrorInSubsequentArgumentSerialization_Helper();
        await Task.Yield(); // get off the helper's inline continuation stack.
        AssertCollectedObject(enumerable);
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("FailsOnMono", "true")]
    public async Task ArgumentEnumerable_ReleasedWhenIgnoredBySuccessfulRpcCall()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ReleasedWhenIgnoredBySuccessfulRpcCall_Helper();
        await Task.Yield(); // get off the helper's inline continuation stack.
        AssertCollectedObject(enumerable);
    }

    [Fact]
    [Trait("GC", "")]
    [Trait("FailsOnMono", "true")]
    public async Task ArgumentEnumerable_ForciblyDisposedAndReleasedWhenNotDisposedWithinRpcCall()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ForciblyDisposedAndReleasedWhenNotDisposedWithinRpcCall_Helper();
        await Task.Yield(); // get off the helper's inline continuation stack.
        AssertCollectedObject(enumerable);

        // Assert that if the RPC server tries to enumerate more values after it returns that it gets the right exception.
        this.server.AllowEnumeratorToContinue.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.server.ArgEnumeratorAfterReturn ?? Task.CompletedTask).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    [Trait("GC", "")]
    public async Task ReturnEnumerable_AutomaticallyReleasedOnErrorFromIteratorMethod()
    {
        WeakReference enumerable = await this.ReturnEnumerable_AutomaticallyReleasedOnErrorFromIteratorMethod_Helper();
        await Task.Yield(); // get off the helper's inline continuation stack.
        AssertCollectedObject(enumerable);
    }

    [Theory]
    [InlineData(1, 0, 2, Server.ValuesReturnedByEnumerables)]
    [InlineData(2, 2, 2, Server.ValuesReturnedByEnumerables)]
    [InlineData(2, 4, 2, Server.ValuesReturnedByEnumerables)]
    [InlineData(2, 2, 4, Server.ValuesReturnedByEnumerables)]
    [InlineData(2, 2, Server.ValuesReturnedByEnumerables, Server.ValuesReturnedByEnumerables)]
    [InlineData(2, 2, Server.ValuesReturnedByEnumerables + 1, Server.ValuesReturnedByEnumerables)]
    [InlineData(2, 2, 2, 0)]
    [InlineData(2, 2, 2, 1)]
    public async Task Prefetch(int minBatchSize, int maxReadAhead, int prefetch, int totalCount)
    {
        var proxy = this.clientRpc.Attach<IServer2>();
        int enumerated = 0;
        await foreach (var item in proxy.GetNumbersParameterizedAsync(minBatchSize, maxReadAhead, prefetch, totalCount, endWithException: false, this.TimeoutToken).WithCancellation(this.TimeoutToken))
        {
            Assert.True(this.server.ActuallyGeneratedValueCount >= Math.Min(totalCount, prefetch), $"Prefetch: {prefetch}, ActuallyGeneratedValueCount: {this.server.ActuallyGeneratedValueCount}");
            Assert.Equal(++enumerated, item);
        }

        Assert.Equal(totalCount, enumerated);
    }

    [Theory]
    [InlineData(1, 0, 0, 2)] // no special features
    [InlineData(1, 0, 3, 2)] // throw during prefetch
    [InlineData(3, 0, 0, 2)] // throw during the first batch
    [InlineData(2, 0, 0, 3)] // throw during the second batch
    public async Task AsyncIteratorThrows(int minBatchSize, int maxReadAhead, int prefetch, int throwAfter)
    {
        var proxy = this.clientRpc.Attach<IServer2>();
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(async delegate
        {
            await foreach (int i in proxy.GetNumbersParameterizedAsync(minBatchSize, maxReadAhead, prefetch, throwAfter, endWithException: true, this.TimeoutToken))
            {
                this.Logger.WriteLine("Observed item: " + i);
            }
        });
        Assert.Equal(Server.FailByDesignExceptionMessage, ex.Message);
    }

    [Fact]
    public async Task EnumerableIdDisposal()
    {
        // This test is specially arranged to create two RPC calls going opposite directions, with the same request ID.
        // By doing so, we can verify that the server doesn't dispose the enumerable until the full sequence is sent to the client.
        this.server.Client = this.serverProxy.Value;
        await foreach (string s in this.clientProxy.Value.CallbackClientAndYieldOneValueAsync(this.TimeoutToken))
        {
        }
    }

    protected abstract void InitializeFormattersAndHandlers();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ArgumentEnumerable_ReleasedOnErrorResponse_Helper()
    {
        IAsyncEnumerable<int>? numbers = new int[] { 1, 2, 3 }.AsAsyncEnumerable();
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync("ThisMethodDoesNotExist", new object?[] { numbers }, this.TimeoutToken));
        WeakReference result = new WeakReference(numbers);
        numbers = null;
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ArgumentEnumerable_ReleasedOnErrorInSubsequentArgumentSerialization_Helper()
    {
        IAsyncEnumerable<int>? numbers = new int[] { 1, 2, 3 }.AsAsyncEnumerable();
        await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeWithCancellationAsync("ThisMethodDoesNotExist", new object?[] { numbers, new UnserializableType() }, this.TimeoutToken));
        WeakReference result = new WeakReference(numbers);
        numbers = null;
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ArgumentEnumerable_ReleasedWhenIgnoredBySuccessfulRpcCall_Helper()
    {
        IAsyncEnumerable<int>? numbers = new int[] { 1, 2, 3 }.AsAsyncEnumerable();
        await this.clientProxy.Value.PassInNumbersAndIgnoreAsync(numbers, this.TimeoutToken);
        WeakReference result = new WeakReference(numbers);
        numbers = null;
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ArgumentEnumerable_ForciblyDisposedAndReleasedWhenNotDisposedWithinRpcCall_Helper()
    {
        IAsyncEnumerable<int>? numbers = new int[] { 1, 2, 3 }.AsAsyncEnumerable();
        await this.clientProxy.Value.PassInNumbersOnlyStartEnumerationAsync(numbers, this.TimeoutToken);
        WeakReference result = new WeakReference(numbers);
        numbers = null;
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ReturnEnumerable_AutomaticallyReleasedOnErrorFromIteratorMethod_Helper()
    {
        this.server.EnumeratedSource = ImmutableList.Create(1, 2, 3);
        WeakReference weakReferenceToSource = new WeakReference(this.server.EnumeratedSource);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);

        // Start up th emethod and get the first item.
        var enumerable = this.clientProxy.Value.GetValuesFromEnumeratedSourceAsync(cts.Token);
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());

        // Now remove the only strong reference to the source object other than what would be captured by the async iterator method.
        this.server.EnumeratedSource = this.server.EnumeratedSource.Clear();

        // Now array for the server method to be canceled
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());

        return weakReferenceToSource;
    }

    [DataContract]
    public class CompoundEnumerableResult
    {
        [DataMember]
        public string? Message { get; set; }

        [DataMember]
        public IAsyncEnumerable<int>? Enumeration { get; set; }
    }

    protected class Server : IServer
    {
        /// <summary>
        /// The number of values produced by the enumerables.
        /// </summary>
        /// <value>This is INTENTIONALLY not a multiple of <see cref="MinBatchSize"/> so we can test gathering the last few elements.</value>
        public const int ValuesReturnedByEnumerables = 7;

        public const int MinBatchSize = 3;

        public const int MaxReadAhead = 4;

        internal const string FailByDesignExceptionMessage = "Fail by design";

        public IClient? Client { get; set; }

        public AsyncManualResetEvent MethodEntered { get; } = new AsyncManualResetEvent();

        public AsyncManualResetEvent MethodExited { get; } = new AsyncManualResetEvent();

        public Task? ArgEnumeratorAfterReturn { get; private set; }

        public AsyncManualResetEvent AllowEnumeratorToContinue { get; } = new AsyncManualResetEvent();

        public int ActuallyGeneratedValueCount { get; private set; }

        public AsyncManualResetEvent ValueGenerated { get; } = new AsyncManualResetEvent();

        public ImmutableList<int> EnumeratedSource { get; set; } = ImmutableList<int>.Empty;

        public async IAsyncEnumerable<int> GetValuesFromEnumeratedSourceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in this.EnumeratedSource)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }

        public IAsyncEnumerable<int> GetNumbersInBatchesAsync(CancellationToken cancellationToken)
            => this.GetNumbersAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = MinBatchSize });

        public IAsyncEnumerable<int> GetNumbersWithReadAheadAsync(CancellationToken cancellationToken)
            => this.GetNumbersAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { MaxReadAhead = MaxReadAhead, MinBatchSize = MinBatchSize });

        public IAsyncEnumerable<int> GetNumbersNoCancellationAsync() => this.GetNumbersAsync(CancellationToken.None);

        public async IAsyncEnumerable<int> GetNumbersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in this.GetNumbersAsync(ValuesReturnedByEnumerables, endWithException: false, cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                this.MethodExited.Set();
            }
        }

        public async IAsyncEnumerable<int> GetNumbersThatWerePassedInAsync(IAsyncEnumerable<int> numbers, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (int number in numbers.WithCancellation(cancellationToken))
            {
                yield return number;
            }
        }

        public IAsyncEnumerable<int> GetNumbersParameterizedAsync(int batchSize, int readAhead, int prefetch, int totalCount, bool endWithException, CancellationToken cancellationToken)
        {
            return this.GetNumbersAsync(totalCount, endWithException, cancellationToken)
                .WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = batchSize, MaxReadAhead = readAhead, Prefetch = prefetch });
        }

        public async IAsyncEnumerable<int> WaitTillCanceledBeforeFirstItemAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<int>();
            await tcs.Task.WithCancellation(cancellationToken);
            yield return 0; // we will never reach this.
        }

        public async Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeFirstItemWithPrefetchAsync(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            return await this.WaitTillCanceledBeforeFirstItemAsync(cancellationToken)
                .WithPrefetchAsync(1, cancellationToken);
        }

        public IAsyncEnumerable<int> WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAsync(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            return this.WaitTillCanceledBeforeFirstItemAsync(cancellationToken)
                .WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 });
        }

        public Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeFirstItemUsingPrefetchSettingAndTaskWrapperAsync(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            return Task.FromResult(this.WaitTillCanceledBeforeFirstItemAsync(cancellationToken)
                .WithJsonRpcSettings(new JsonRpcEnumerableSettings { Prefetch = 1 }));
        }

        public Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeReturningAsync(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            var tcs = new TaskCompletionSource<IAsyncEnumerable<int>>();
            return tcs.Task.WithCancellation(cancellationToken);
        }

        public async Task PassInNumbersAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken)
        {
            int realizedValuesCount = 0;
            await foreach (int number in numbers)
            {
                realizedValuesCount++;
            }

            Assert.Equal(ValuesReturnedByEnumerables, realizedValuesCount);
        }

        public Task PassInNumbersAndIgnoreAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task PassInNumbersOnlyStartEnumerationAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken)
        {
            var asyncEnum = numbers.GetAsyncEnumerator(cancellationToken);
            await asyncEnum.MoveNextAsync();
            this.ArgEnumeratorAfterReturn = Task.Run(
                async delegate
                {
                    await this.AllowEnumeratorToContinue.WaitAsync();
                    await asyncEnum.MoveNextAsync();
                },
                CancellationToken.None);
        }

        public Task<CompoundEnumerableResult> GetNumbersAndMetadataAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new CompoundEnumerableResult
            {
                Message = "Hello!",
                Enumeration = this.GetNumbersAsync(cancellationToken),
            });
        }

        public async IAsyncEnumerable<string> CallbackClientAndYieldOneValueAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (this.Client is null)
            {
                throw new InvalidOperationException("Client must be set before calling this method.");
            }

            // We deliberately make a callback right away such that the request ID for it collides with the request ID that served THIS request.
            await this.Client.DoSomethingAsync(cancellationToken);
            yield return "Hello";
        }

        private async IAsyncEnumerable<int> GetNumbersAsync(int totalCount, bool endWithException, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int i = 1; i <= totalCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                this.ActuallyGeneratedValueCount++;
                this.ValueGenerated.PulseAll();
                yield return i;
            }

            if (endWithException)
            {
                throw new InvalidOperationException(FailByDesignExceptionMessage);
            }
        }
    }

    protected class Client : IClient
    {
        public Task DoSomethingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [JsonConverter(typeof(ThrowingJsonConverter<UnserializableType>))]
    [System.Text.Json.Serialization.JsonConverter(typeof(ThrowingSystemTextJsonConverter<UnserializableType>))]
    [MessagePackFormatter(typeof(ThrowingMessagePackFormatter<UnserializableType>))]
    [NBMP.MessagePackConverter(typeof(ThrowingMessagePackNerdbankConverter<UnserializableType>))]
    protected class UnserializableType
    {
    }

    protected class ThrowingJsonConverter<T> : JsonConverter<T>
    {
        public override T ReadJson(JsonReader reader, Type objectType, T? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new Exception();
        }

        public override void WriteJson(JsonWriter writer, T? value, JsonSerializer serializer)
        {
            throw new Exception();
        }
    }

    protected class ThrowingMessagePackFormatter<T> : IMessagePackFormatter<T>
    {
        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            throw new Exception();
        }

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            throw new Exception();
        }
    }

    protected class ThrowingMessagePackNerdbankConverter<T> : NBMP.MessagePackConverter<T>
    {
        public override T? Read(ref NBMP.MessagePackReader reader, NBMP.SerializationContext context)
        {
            throw new Exception();
        }

        public override void Write(ref NBMP.MessagePackWriter writer, in T? value, NBMP.SerializationContext context)
        {
            throw new Exception();
        }
    }

    protected class ThrowingSystemTextJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<T>
    {
        public override T? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            throw new Exception();
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, T value, System.Text.Json.JsonSerializerOptions options)
        {
            throw new Exception();
        }
    }
}
