// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using PolyType;

public abstract partial class TestBase : IDisposable
{
    protected static readonly TimeSpan ExpectedTimeout = TimeSpan.FromMilliseconds(200);

    protected static readonly TimeSpan UnexpectedTimeout = Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(20);

    protected static readonly CancellationToken PrecanceledToken = new CancellationToken(canceled: true);

    protected static readonly bool IsRunningUnderLiveUnitTest = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Microsoft.CodeAnalysis.LiveUnitTesting.Runtime");

    private const int GCAllocationAttempts = 10;

    private readonly CancellationTokenSource timeoutTokenSource;

    protected TestBase(ITestOutputHelper logger)
    {
        this.Logger = logger;
        this.timeoutTokenSource = new CancellationTokenSource(TestTimeout);
        this.timeoutTokenSource.Token.Register(() => this.Logger.WriteLine($"TEST TIMEOUT: {nameof(TestBase)}.{nameof(this.TimeoutToken)} has been canceled due to the test exceeding the {TestTimeout} time limit."));
    }

    protected static CancellationToken ExpectedTimeoutToken => new CancellationTokenSource(ExpectedTimeout).Token;

    protected static CancellationToken UnexpectedTimeoutToken => new CancellationTokenSource(UnexpectedTimeout).Token;

    protected static bool IsTestRunOnAzurePipelines => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYSTEM_DEFINITIONID"));

    protected ITestOutputHelper Logger { get; }

    protected CancellationToken TimeoutToken => Debugger.IsAttached ? CancellationToken.None : this.timeoutTokenSource.Token;

    private static TimeSpan TestTimeout => UnexpectedTimeout;

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected static void AssertCollectedObject(WeakReference weakReference)
    {
        GC.Collect();

        // For some reason the assertion tends to be sketchy when running on Azure Pipelines.
        if (IsTestRunOnAzurePipelines)
        {
            Skip.If(weakReference.IsAlive);
        }

        Assert.False(weakReference.IsAlive);
    }

    /// <summary>
    /// Checks whether a given exception or any transitive inner exception has a given type.
    /// </summary>
    /// <typeparam name="TException">The type of exception sought.</typeparam>
    /// <param name="ex">The exception to search.</param>
    /// <param name="exactTypeMatch"><see langword="true"/> to require an exact match; <see langword="false"/> to allow match on derived types of <typeparamref name="TException"/>.</param>
    /// <returns><see langword="true"/> if an exception of type <typeparamref name="TException"/> was found at or under <paramref name="ex"/>.</returns>
    protected static bool IsExceptionOrInnerOfType<TException>(Exception? ex, bool exactTypeMatch = false) => FindExceptionOrInner(ex, x => exactTypeMatch ? (x.GetType() == typeof(TException)) : x is TException) is object;

    /// <summary>
    /// Searches an exception and all inner exceptions for one that matches some check.
    /// </summary>
    /// <param name="ex">The starting exception to search.</param>
    /// <param name="predicate">The test for whether this exception is the one sought for.</param>
    /// <returns>The first in the exception tree for which <paramref name="predicate"/> returns <see langword="true"/>, or <see langword="null"/> if the <paramref name="predicate"/> never returned <see langword="true"/>.</returns>
    protected static Exception? FindExceptionOrInner(Exception? ex, Func<Exception, bool> predicate)
    {
        if (ex is null)
        {
            return null;
        }

        if (predicate(ex))
        {
            return ex;
        }

        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Select(inner => FindExceptionOrInner(inner, predicate)).FirstOrDefault(inner => inner is object);
        }

        return FindExceptionOrInner(ex.InnerException, predicate);
    }

    protected static Task WhenAllSucceedOrAnyFault(params Task[] tasks)
    {
        if (tasks.Length == 0)
        {
            return Task.CompletedTask;
        }

        var resultTcs = new TaskCompletionSource<object>();
        Task.WhenAll(tasks).ApplyResultTo(resultTcs);
        foreach (Task task in tasks)
        {
            task.ContinueWith((failedTask, state) => ((TaskCompletionSource<object>)state!).TrySetException(failedTask.Exception!), resultTcs, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default).Forget();
        }

        return resultTcs.Task;
    }

    protected static T BinaryFormatterRoundtrip<T>(T original)
        where T : notnull, ISerializable
    {
        Assert.NotNull(typeof(T).GetCustomAttribute<SerializableAttribute>());

#pragma warning disable SYSLIB0050
        StreamingContext context = new(StreamingContextStates.Remoting);
        SerializationInfo info = new SerializationInfo(typeof(T), new RoundtripFormatter());
        original.GetObjectData(info, new StreamingContext(StreamingContextStates.Remoting));
#pragma warning restore SYSLIB0050
        ConstructorInfo? ctor = typeof(T).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(SerializationInfo), typeof(StreamingContext)],
            modifiers: null);
        Assert.NotNull(ctor);
        return (T)ctor.Invoke([info, context])!;
    }

    protected async Task AssertWeakReferenceGetsCollectedAsync(WeakReference weakReference)
    {
        while (!this.TimeoutToken.IsCancellationRequested && weakReference.IsAlive)
        {
            await Task.Delay(1);
            GC.Collect();
        }

        Assert.False(weakReference.IsAlive);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.timeoutTokenSource.Dispose();
        }
    }

    /// <summary>
    /// Runs a given scenario many times to observe memory characteristics and assert that they can satisfy given conditions.
    /// </summary>
    /// <param name="scenario">The delegate to invoke.</param>
    /// <param name="maxBytesAllocated">The maximum number of bytes allowed to be allocated by one run of the scenario. Use -1 to indicate no limit.</param>
    /// <param name="iterations">The number of times to invoke <paramref name="scenario"/> in a row before measuring average memory impact.</param>
    /// <param name="allowedAttempts">The number of times the (scenario * iterations) loop repeats with a failing result before ultimately giving up.</param>
    /// <returns>A task that captures the result of the operation.</returns>
    protected virtual async Task CheckGCPressureAsync(Func<Task> scenario, int maxBytesAllocated = -1, int iterations = 100, int allowedAttempts = GCAllocationAttempts)
    {
        // prime the pump
        for (int i = 0; i < 3; i++)
        {
            await scenario();
        }

        // This test is rather rough.  So we're willing to try it a few times in order to observe the desired value.
        bool passingAttemptObserved = false;
        for (int attempt = 1; attempt <= allowedAttempts; attempt++)
        {
            this.Logger?.WriteLine($"Attempt {attempt}");
            long initialMemory = GC.GetTotalMemory(forceFullCollection: true);
            for (int i = 0; i < iterations; i++)
            {
                await scenario();
            }

            long allocated = (GC.GetTotalMemory(forceFullCollection: false) - initialMemory) / iterations;
            long leaked = (GC.GetTotalMemory(forceFullCollection: true) - initialMemory) / iterations;

            this.Logger?.WriteLine($"{leaked} bytes leaked per iteration.", leaked);
            this.Logger?.WriteLine($"{allocated} bytes allocated per iteration ({(maxBytesAllocated >= 0 ? (object)maxBytesAllocated : "unlimited")} allowed).");

            if (leaked <= 0 && (allocated <= maxBytesAllocated || maxBytesAllocated < 0))
            {
                passingAttemptObserved = true;
                break;
            }

            if (!passingAttemptObserved)
            {
                // give the system a bit of cool down time to increase the odds we'll pass next time.
                GC.Collect();
                await Task.Delay(250);
            }
        }

        Assert.True(passingAttemptObserved);
    }

    [GenerateShape<FormatterTestBase<NerdbankMessagePackFormatter>.CustomType>]
    internal partial class TestBaseWitness;

#pragma warning disable SYSLIB0050 // Type or member is obsolete
    private class RoundtripFormatter : IFormatterConverter
#pragma warning restore SYSLIB0050 // Type or member is obsolete
    {
        public object Convert(object value, Type type) => value;

        public object Convert(object value, TypeCode typeCode) => value;

        public bool ToBoolean(object value) => (bool)value;

        public byte ToByte(object value) => (byte)value;

        public char ToChar(object value) => (char)value;

        public DateTime ToDateTime(object value) => (DateTime)value;

        public decimal ToDecimal(object value) => (decimal)value;

        public double ToDouble(object value) => (double)value;

        public short ToInt16(object value) => (short)value;

        public int ToInt32(object value) => (int)value;

        public long ToInt64(object value) => (long)value;

        public sbyte ToSByte(object value) => (sbyte)value;

        public float ToSingle(object value) => (float)value;

        public string? ToString(object value) => (string)value;

        public ushort ToUInt16(object value) => (ushort)value;

        public uint ToUInt32(object value) => (uint)value;

        public ulong ToUInt64(object value) => (ulong)value;
    }
}
