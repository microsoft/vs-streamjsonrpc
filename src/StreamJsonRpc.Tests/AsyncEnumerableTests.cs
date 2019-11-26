// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Formatters;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public abstract class AsyncEnumerableTests : TestBase, IAsyncLifetime
{
    protected readonly Server server = new Server();
    protected JsonRpc serverRpc;
    protected IJsonRpcMessageFormatter serverMessageFormatter;

    protected Lazy<IServer> clientProxy;
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
    /// since the server implements the one method on this interface with a return type of Task{T}
    /// but we want the client proxy to NOT be that.
    /// </summary>
    protected interface IServer2
    {
        IAsyncEnumerable<int> WaitTillCanceledBeforeReturningAsync(CancellationToken cancellationToken);
    }

    protected interface IServer
    {
        IAsyncEnumerable<int> GetNumbersInBatchesAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersWithReadAheadAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> GetNumbersAsync(CancellationToken cancellationToken);

        IAsyncEnumerable<int> WaitTillCanceledBeforeFirstItemAsync(CancellationToken cancellationToken);

        Task<IAsyncEnumerable<int>> WaitTillCanceledBeforeReturningAsync(CancellationToken cancellationToken);

        Task<CompoundEnumerableResult> GetNumbersAndMetadataAsync(CancellationToken cancellationToken);

        Task PassInNumbersAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);

        Task PassInNumbersAndIgnoreAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);

        Task PassInNumbersOnlyStartEnumerationAsync(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken);
    }

    public Task InitializeAsync()
    {
        Tuple<Nerdbank.FullDuplexStream, Nerdbank.FullDuplexStream> streams = Nerdbank.FullDuplexStream.CreateStreams();

        this.InitializeFormattersAndHandlers();

        var serverHandler = new LengthHeaderMessageHandler(streams.Item1.UsePipe(), this.serverMessageFormatter);
        var clientHandler = new LengthHeaderMessageHandler(streams.Item2.UsePipe(), this.clientMessageFormatter);

        this.serverRpc = new JsonRpc(serverHandler, this.server);
        this.clientRpc = new JsonRpc(clientHandler);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Information);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Information);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();

        this.clientProxy = new Lazy<IServer>(() => this.clientRpc.Attach<IServer>());

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        this.serverRpc.Dispose();
        this.clientRpc.Dispose();

        if (this.serverRpc.Completion.IsFaulted)
        {
            this.Logger.WriteLine("Server faulted with: " + this.serverRpc.Completion.Exception);
        }

        return Task.CompletedTask;
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
                Assert.True(valueTask.GetAwaiter().GetResult());
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
        async IAsyncEnumerable<int> Generator(CancellationToken cancellationToken)
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNextTask);
    }

    [Theory]
    [PairwiseData]
    public async Task Cancellation_DuringLongRunningServerBeforeReturning(bool useProxy)
    {
        var cts = new CancellationTokenSource();
        Task<IAsyncEnumerable<int>> enumerable = useProxy
            ? this.clientProxy.Value.WaitTillCanceledBeforeReturningAsync(cts.Token)
            : this.clientRpc.InvokeWithCancellationAsync<IAsyncEnumerable<int>>(nameof(Server.WaitTillCanceledBeforeReturningAsync), cancellationToken: cts.Token);

        // Make sure the method has been invoked first.
        await this.server.MethodEntered.WaitAsync(this.TimeoutToken);

        // Now cancel the server method to get it to throw OCE.
        cts.Cancel();

        // Verify that it does throw OCE. Or timeout and fail the test if it doesn't.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerable).WithCancellation(this.TimeoutToken);
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
        await Assert.ThrowsAsync<NotSupportedException>(() => this.clientRpc.NotifyAsync(nameof(Server.PassInNumbersAsync), new object?[] { numbers }));
        await Assert.ThrowsAsync<NotSupportedException>(() => this.clientRpc.NotifyAsync(nameof(Server.PassInNumbersAsync), new object?[] { new { e = numbers } }));
    }

    [SkippableFact]
    [Trait("GC", "")]
    public async Task ArgumentEnumerable_ReleasedOnErrorResponse()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ReleasedOnErrorResponse_Helper();
        AssertCollectedObject(enumerable);
    }

    [SkippableFact]
    [Trait("GC", "")]
    public async Task ArgumentEnumerable_ReleasedOnErrorInSubsequentArgumentSerialization()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ReleasedOnErrorInSubsequentArgumentSerialization_Helper();
        AssertCollectedObject(enumerable);
    }

    [SkippableFact]
    [Trait("GC", "")]
    public async Task ArgumentEnumerable_ReleasedWhenIgnoredBySuccessfulRpcCall()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ReleasedWhenIgnoredBySuccessfulRpcCall_Helper();
        AssertCollectedObject(enumerable);
    }

    [SkippableFact]
    [Trait("GC", "")]
    public async Task ArgumentEnumerable_ForciblyDisposedAndReleasedWhenNotDisposedWithinRpcCall()
    {
        WeakReference enumerable = await this.ArgumentEnumerable_ForciblyDisposedAndReleasedWhenNotDisposedWithinRpcCall_Helper();
        AssertCollectedObject(enumerable);

        // Assert that if the RPC server tries to enumerate more values after it returns that it gets the right exception.
        this.server.AllowEnumeratorToContinue.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.server.ArgEnumeratorAfterReturn).WithCancellation(this.TimeoutToken);
    }

    protected abstract void InitializeFormattersAndHandlers();

    private static void AssertCollectedObject(WeakReference weakReference)
    {
        GC.Collect();

        // For some reason the assertion tends to be sketchy when running on Azure Pipelines.
        if (IsTestRunOnAzurePipelines)
        {
            Skip.If(weakReference.IsAlive);
        }

        Assert.False(weakReference.IsAlive);
    }

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
        await Assert.ThrowsAsync<Exception>(() => this.clientRpc.InvokeWithCancellationAsync("ThisMethodDoesNotExist", new object?[] { numbers, new UnserializableType() }, this.TimeoutToken));
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

    protected class Server : IServer
    {
        /// <summary>
        /// The number of values produced by the enumerables.
        /// </summary>
        /// <value>This is INTENTIONALLY not a multiple of <see cref="MinBatchSize"/> so we can test gathering the last few elements.</value>
        public const int ValuesReturnedByEnumerables = 7;

        public const int MinBatchSize = 3;

        public const int MaxReadAhead = 4;

        public AsyncManualResetEvent MethodEntered { get; } = new AsyncManualResetEvent();

        public AsyncManualResetEvent MethodExited { get; } = new AsyncManualResetEvent();

        public Task? ArgEnumeratorAfterReturn { get; private set; }

        public AsyncManualResetEvent AllowEnumeratorToContinue { get; } = new AsyncManualResetEvent();

        public int ActuallyGeneratedValueCount { get; private set; }

        public AsyncManualResetEvent ValueGenerated { get; } = new AsyncManualResetEvent();

        public IAsyncEnumerable<int> GetNumbersInBatchesAsync(CancellationToken cancellationToken)
            => this.GetNumbersAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { MinBatchSize = MinBatchSize });

        public IAsyncEnumerable<int> GetNumbersWithReadAheadAsync(CancellationToken cancellationToken)
            => this.GetNumbersAsync(cancellationToken).WithJsonRpcSettings(new JsonRpcEnumerableSettings { MaxReadAhead = MaxReadAhead, MinBatchSize = MinBatchSize });

        public async IAsyncEnumerable<int> GetNumbersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                for (int i = 1; i <= ValuesReturnedByEnumerables; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    this.ActuallyGeneratedValueCount++;
                    this.ValueGenerated.PulseAll();
                    yield return i;
                }
            }
            finally
            {
                this.MethodExited.Set();
            }
        }

        public async IAsyncEnumerable<int> WaitTillCanceledBeforeFirstItemAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<int>();
            await tcs.Task.WithCancellation(cancellationToken);
            yield return 0; // we will never reach this.
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
            this.ArgEnumeratorAfterReturn = Task.Run(async delegate
            {
                await this.AllowEnumeratorToContinue.WaitAsync();
                await asyncEnum.MoveNextAsync();
            });
        }

        public Task<CompoundEnumerableResult> GetNumbersAndMetadataAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new CompoundEnumerableResult
            {
                Message = "Hello!",
                Enumeration = this.GetNumbersAsync(cancellationToken),
            });
        }
    }

    [DataContract]
    protected class CompoundEnumerableResult
    {
        [DataMember]
        public string? Message { get; set; }

        [DataMember]
        public IAsyncEnumerable<int>? Enumeration { get; set; }
    }

    [JsonConverter(typeof(ThrowingJsonConverter<UnserializableType>))]
    [MessagePackFormatter(typeof(ThrowingMessagePackFormatter<UnserializableType>))]
    protected class UnserializableType
    {
    }

    protected class ThrowingJsonConverter<T> : JsonConverter<T>
    {
        public override T ReadJson(JsonReader reader, Type objectType, T existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new Exception();
        }

        public override void WriteJson(JsonWriter writer, T value, JsonSerializer serializer)
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
}
