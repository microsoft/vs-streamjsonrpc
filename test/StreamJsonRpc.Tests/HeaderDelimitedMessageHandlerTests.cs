// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc.Reflection;

public class HeaderDelimitedMessageHandlerTests : TestBase
{
    private const string CRLF = "\r\n";
    private Stream sendingStream = new MemoryStream();
    private MemoryStream receivingStream = new MemoryStream();
    private HeaderDelimitedMessageHandler handler;

    public HeaderDelimitedMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
        // Use strict pipe writer so we get deterministic writes for consistent testing.
        this.handler = new HeaderDelimitedMessageHandler(
            PipeWriter.Create(this.sendingStream, new StreamPipeWriterOptions(leaveOpen: true)),
            PipeReader.Create(this.receivingStream, new StreamPipeReaderOptions(leaveOpen: true)),
            CreateAnyJsonFormatter());
    }

    [Fact]
    public async Task SubType_ForcesHeader()
    {
        this.handler.SubType = "nonstandard";
        await this.handler.WriteAsync(new JsonRpcRequest { Method = "test" }, this.TimeoutToken);
        this.sendingStream.Position = 0;
        var sr = new StreamReader(this.sendingStream, this.handler.Encoding);
        string writtenContent = sr.ReadToEnd();
        Assert.Contains(this.handler.SubType, writtenContent);
    }

    [Fact]
    public void EncodingThrowsForNonTextFormatters()
    {
        this.handler = new HeaderDelimitedMessageHandler(
            PipeWriter.Create(this.sendingStream, new StreamPipeWriterOptions(leaveOpen: true)),
            PipeReader.Create(this.receivingStream, new StreamPipeReaderOptions(leaveOpen: true)),
            new MockFormatter());
        Assert.Throws<NotSupportedException>(() => this.handler.Encoding);
        Assert.Throws<NotSupportedException>(() => this.handler.Encoding = Encoding.UTF8);
    }

    [Fact]
    public async Task ReadCoreAsync_HandlesSpacingCorrectly()
    {
        string content =
"Content-Length:  33   " + CRLF +
"Content-Type: application/vscode-jsonrpc;charset=utf-8" + CRLF +
CRLF +
"{\"jsonrpc\":\"2.0\",\"method\":\"test\"}";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        var readContent = (JsonRpcRequest?)await this.handler.ReadAsync(CancellationToken.None);
        Assert.Equal("test", readContent!.Method);
        (this.handler as IJsonRpcMessageBufferManager)?.DeserializationComplete(readContent);

        this.receivingStream.Position = 0;
        this.receivingStream.SetLength(0);

        content =
"Content-Length:33" + CRLF +
CRLF +
"{\"jsonrpc\":\"2.0\",\"method\":\"test\"}";
        bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        readContent = (JsonRpcRequest?)await this.handler.ReadAsync(CancellationToken.None);
        Assert.Equal("test", readContent!.Method);
        (this.handler as IJsonRpcMessageBufferManager)?.DeserializationComplete(readContent);
    }

    [Fact]
    public async Task ReadCoreAsync_HandlesUtf8CharsetCorrectly()
    {
        // Using 'utf8'
        string content =
"Content-Length: 33" + CRLF +
"Content-Type: application/vscode-jsonrpc;charset=utf8" + CRLF +
CRLF +
"{\"jsonrpc\":\"2.0\",\"method\":\"test\"}";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        var readContent = (JsonRpcRequest?)await this.handler.ReadAsync(CancellationToken.None);
        Assert.Equal("test", readContent!.Method);
        (this.handler as IJsonRpcMessageBufferManager)?.DeserializationComplete(readContent);

        this.receivingStream.Position = 0;
        this.receivingStream.SetLength(0);

        // Using 'utf-8'
        content =
"Content-Length: 33" + CRLF +
"Content-Type: application/vscode-jsonrpc;charset=utf-8" + CRLF +
CRLF +
"{\"jsonrpc\":\"2.0\",\"method\":\"test\"}";
        bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        readContent = (JsonRpcRequest?)await this.handler.ReadAsync(CancellationToken.None);
        Assert.Equal("test", readContent!.Method);
        (this.handler as IJsonRpcMessageBufferManager)?.DeserializationComplete(readContent);
    }

    [Fact]
    public async Task DisposeReader_WaitsForDeserializationCompleteToAdvance()
    {
        string body = "{\"jsonrpc\":\"2.0\",\"method\":\"test\"}";
        byte[] content = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}{CRLF}{CRLF}{body}");
        using var stream = new MemoryStream(content);
        using var reader = new GatedPipeReader(PipeReader.Create(stream));
        var handler = new HeaderDelimitedMessageHandler(null, reader, new SystemTextJsonFormatter());
        JsonRpcMessage message = (await handler.ReadAsync(this.TimeoutToken))!;
        Assert.IsAssignableFrom<IJsonRpcMessageBufferManager>(message);

        reader.PauseNextAdvance();
        Task deserialization = Task.Run(() => ((IJsonRpcMessageBufferManager)handler).DeserializationComplete(message), TestContext.Current.CancellationToken);
        Task? disposal = null;
        try
        {
            reader.AdvanceEntered.Wait(this.TimeoutToken);
            var disposalStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = Task.Run(
                async () =>
                {
                    disposalStarted.SetResult(true);
                    await handler.DisposeAsync();
                },
                TestContext.Current.CancellationToken);
            await disposalStarted.Task.WithCancellation(this.TimeoutToken);
            await Task.Delay(ExpectedTimeout, this.TimeoutToken);
            Assert.False(reader.CompleteCalled.IsSet);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            reader.AllowAdvance.Set();
        }

        await Task.WhenAll(deserialization, disposal!).WithCancellation(this.TimeoutToken);
        Assert.True(reader.CompleteCalled.IsSet);
    }

    [Fact]
    public void TooLargeHeader()
    {
        Assert.Throws<ArgumentException>(() => this.handler.SubType = new string('a', 980));
    }

    private class MockFormatter : IJsonRpcMessageFormatter
    {
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            throw new NotImplementedException();
        }

        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            throw new NotImplementedException();
        }

        public object GetJsonText(JsonRpcMessage message)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class GatedPipeReader : PipeReader, IDisposable
    {
        private readonly PipeReader inner;
        private int pauseNextAdvance;

        internal GatedPipeReader(PipeReader inner) => this.inner = inner;

        internal ManualResetEventSlim AdvanceEntered { get; } = new();

        internal ManualResetEventSlim AllowAdvance { get; } = new();

        internal ManualResetEventSlim CompleteCalled { get; } = new();

        public override void AdvanceTo(SequencePosition consumed) => this.AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (Interlocked.Exchange(ref this.pauseNextAdvance, 0) != 0)
            {
                this.AdvanceEntered.Set();
                this.AllowAdvance.Wait(UnexpectedTimeout);
            }

            this.inner.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead() => this.inner.CancelPendingRead();

        public override void Complete(Exception? exception = null)
        {
            this.CompleteCalled.Set();
            this.inner.Complete(exception);
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => this.inner.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result) => this.inner.TryRead(out result);

        public void Dispose()
        {
            this.AdvanceEntered.Dispose();
            this.AllowAdvance.Dispose();
            this.CompleteCalled.Dispose();
        }

        internal void PauseNextAdvance() => Interlocked.Exchange(ref this.pauseNextAdvance, 1);
    }
}
