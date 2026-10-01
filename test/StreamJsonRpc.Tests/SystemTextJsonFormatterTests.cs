// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nerdbank.Streams;

public class SystemTextJsonFormatterTests : FormatterTestBase<SystemTextJsonFormatter>
{
    public SystemTextJsonFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void STJAttributesWinOverDataContractAttributesByDefault()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
        requestMessage.Method = "test";
        requestMessage.Arguments = new[] { new DCSClass { C = 1 } };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, requestMessage);

        using JsonDocument doc = JsonDocument.Parse(sequence);
        this.Logger.WriteLine(doc.RootElement.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("B").GetInt32());
    }

    [Fact]
    public void STJAttributesWinOverDataMemberWithoutDataContract()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
        requestMessage.Method = "test";
        requestMessage.Arguments = new[] { new STJClass { C = 1 } };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, requestMessage);

        using JsonDocument doc = JsonDocument.Parse(sequence);
        this.Logger.WriteLine(doc.RootElement.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("B").GetInt32());
    }

    [Fact]
    public void FailedSerializationDoesNotPoisonCachedWriter()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest initialMessage = messageFactory.CreateRequestMessage();
        initialMessage.Method = "prime";
        using Sequence<byte> initialOutput = new();
        this.Formatter.Serialize(initialOutput, initialMessage);

        JsonRpcError invalidMessage = messageFactory.CreateErrorMessage();
        using Sequence<byte> invalidOutput = new();
        Assert.Throws<JsonException>(() => this.Formatter.Serialize(invalidOutput, invalidMessage));

        JsonRpcRequest validMessage = messageFactory.CreateRequestMessage();
        validMessage.Method = "test";
        using Sequence<byte> validOutput = new();
        this.Formatter.Serialize(validOutput, validMessage);

        using JsonDocument document = JsonDocument.Parse(validOutput);
        Assert.Equal("test", document.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task ConcurrentSerializationUsesIndependentWriters()
    {
        using ManualResetEventSlim firstWriteStarted = new();
        using ManualResetEventSlim allowFirstWriteToComplete = new();
        this.Formatter.JsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new BlockingValueConverter(firstWriteStarted, allowFirstWriteToComplete) },
        };

        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest initialMessage = messageFactory.CreateRequestMessage();
        initialMessage.Method = "prime";
        using Sequence<byte> initialOutput = new();
        this.Formatter.Serialize(initialOutput, initialMessage);

        JsonRpcRequest firstMessage = messageFactory.CreateRequestMessage();
        firstMessage.Method = "test";
        firstMessage.Arguments = new[] { new BlockingValue("first") };

        JsonRpcRequest secondMessage = messageFactory.CreateRequestMessage();
        secondMessage.Method = "test";
        secondMessage.Arguments = new[] { new BlockingValue("second") };

        using Sequence<byte> firstOutput = new();
        using Sequence<byte> secondOutput = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task firstSerialization = Task.Run(() => this.Formatter.Serialize(firstOutput, firstMessage), cancellationToken);
        try
        {
            Assert.True(firstWriteStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Task secondSerialization = Task.Run(() => this.Formatter.Serialize(secondOutput, secondMessage), cancellationToken);
            await WaitWithTimeoutAsync(secondSerialization, cancellationToken);
        }
        finally
        {
            allowFirstWriteToComplete.Set();
        }

        await WaitWithTimeoutAsync(firstSerialization, cancellationToken);

        using JsonDocument firstDocument = JsonDocument.Parse(firstOutput);
        using JsonDocument secondDocument = JsonDocument.Parse(secondOutput);
        Assert.Equal("first", firstDocument.RootElement.GetProperty("params")[0].GetString());
        Assert.Equal("second", secondDocument.RootElement.GetProperty("params")[0].GetString());
    }

    [Fact]
    public void CachedWriterDoesNotRetainOutputBuffer()
    {
        WeakReference outputBuffer = SerializeToShortLivedBuffer(this.Formatter);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.KeepAlive(this.Formatter);

        Assert.False(outputBuffer.IsAlive);
    }

    protected override SystemTextJsonFormatter CreateFormatter() => new();

    private static async Task WaitWithTimeoutAsync(Task task, CancellationToken cancellationToken)
    {
        Task timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        Task completed = await Task.WhenAny(task, timeout);
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Same(task, completed);
        await task;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SerializeToShortLivedBuffer(SystemTextJsonFormatter formatter)
    {
        Sequence<byte> output = new();
        JsonRpcRequest message = ((IJsonRpcMessageFactory)formatter).CreateRequestMessage();
        message.Method = "test";
        formatter.Serialize(output, message);
        return new WeakReference(output);
    }

    [DataContract]
    public class DCSClass
    {
        [DataMember(Name = "A")]
        [JsonPropertyName("B")]
        public int C { get; set; }
    }

    public class STJClass
    {
        [DataMember(Name = "A")]
        [JsonPropertyName("B")]
        public int C { get; set; }
    }

    private sealed record BlockingValue(string Value);

    private sealed class BlockingValueConverter : JsonConverter<BlockingValue>
    {
        private readonly ManualResetEventSlim firstWriteStarted;
        private readonly ManualResetEventSlim allowFirstWriteToComplete;

        internal BlockingValueConverter(ManualResetEventSlim firstWriteStarted, ManualResetEventSlim allowFirstWriteToComplete)
        {
            this.firstWriteStarted = firstWriteStarted;
            this.allowFirstWriteToComplete = allowFirstWriteToComplete;
        }

        public override BlockingValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, BlockingValue value, JsonSerializerOptions options)
        {
            if (value.Value == "first")
            {
                this.firstWriteStarted.Set();
                if (!this.allowFirstWriteToComplete.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException();
                }
            }

            writer.WriteStringValue(value.Value);
        }
    }
}
