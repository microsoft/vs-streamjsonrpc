// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class HeaderDelimitedMessageHandlerTests : TestBase
{
    private const string CRLF = "\r\n";
    private Stream sendingStream = new MemoryStream();
    private MemoryStream receivingStream = new MemoryStream();
    private HeaderDelimitedMessageHandler handler;

    public HeaderDelimitedMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.handler = new HeaderDelimitedMessageHandler(this.sendingStream, this.receivingStream);
    }

    [Fact]
    public async Task SubType_ForcesHeader()
    {
        this.handler.SubType = "nonstandard";
        await this.handler.WriteAsync("hello", this.TimeoutToken);
        this.sendingStream.Position = 0;
        var sr = new StreamReader(this.sendingStream, this.handler.Encoding);
        string writtenContent = sr.ReadToEnd();
        Assert.Contains(this.handler.SubType, writtenContent);
    }

    [Fact]
    public void ReadCoreAsync_HandlesSpacingCorrectly()
    {
        string content =
"Content-Length:  10   " + CRLF +
"Content-Type: application/vscode-jsonrpc;charset=utf-8" + CRLF +
CRLF +
"0123456789";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        string readContent = this.handler.ReadAsync(default(CancellationToken)).GetAwaiter().GetResult();
        Assert.Equal<string>("0123456789", readContent);

        this.receivingStream.Position = 0;
        this.receivingStream.SetLength(0);

        content =
"Content-Length:5" + CRLF +
CRLF +
"ABCDE";
        bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        readContent = this.handler.ReadAsync(default(CancellationToken)).GetAwaiter().GetResult();
        Assert.Equal<string>("ABCDE", readContent);
    }

    [Fact]
    public void ReadCoreAsync_HandlesUtf8CharsetCorrectly()
    {
        // Using 'utf8'
        string content =
"Content-Length: 10" + CRLF +
"Content-Type: application/vscode-jsonrpc;charset=utf8" + CRLF +
CRLF +
"0123456789";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        string readContent = this.handler.ReadAsync(default(CancellationToken)).GetAwaiter().GetResult();
        Assert.Equal<string>("0123456789", readContent);

        this.receivingStream.Position = 0;
        this.receivingStream.SetLength(0);

        // Using 'utf-8'
        content =
"Content-Length: 10" + CRLF +
"Content-Type: application/vscode-jsonrpc;charset=utf-8" + CRLF +
CRLF +
"ABCDEFGHIJ";
        bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        readContent = this.handler.ReadAsync(default(CancellationToken)).GetAwaiter().GetResult();
        Assert.Equal<string>("ABCDEFGHIJ", readContent);
    }

    /// <summary>
    /// Confirms that sending several messages with headers that exceed the built-in buffer size
    /// will not cause corruption.
    /// </summary>
    [Fact]
    public async Task LargeHeader()
    {
        this.sendingStream = new SlowWriteStream();
        this.handler = new HeaderDelimitedMessageHandler(this.sendingStream, this.receivingStream);

        this.handler.SubType = new string('a', 980);
        this.handler.Encoding = Encoding.ASCII;

        var writeTasks = new List<Task>(3);
        for (int i = 0; i < writeTasks.Capacity; i++)
        {
            writeTasks.Add(this.handler.WriteAsync("my content " + i, this.TimeoutToken));
        }

        await Task.WhenAll(writeTasks);
        this.sendingStream.Position = 0;
        var sr = new StreamReader(this.sendingStream, this.handler.Encoding);
        this.Logger.WriteLine(sr.ReadToEnd());
        this.sendingStream.Position = 0;
        await this.sendingStream.CopyToAsync(this.receivingStream, 500, this.TimeoutToken);
        this.receivingStream.Position = 0;

        for (int i = 0; i < writeTasks.Capacity; i++)
        {
            string content = await this.handler.ReadAsync(this.TimeoutToken);
            Assert.Equal("my content " + i, content);
        }
    }

    private class SlowWriteStream : Stream
    {
        private readonly AsyncSemaphore semaphore = new AsyncSemaphore(1);
        private readonly MemoryStream inner = new MemoryStream();

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => this.inner.Length;

        public override long Position { get => this.inner.Position; set => this.inner.Position = value; }

        public override void Flush() => this.inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => this.inner.Seek(offset, origin);

        public override void SetLength(long value) => this.inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => this.inner.Write(buffer, offset, count);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using (await this.semaphore.EnterAsync(cancellationToken))
            {
                await Task.Delay(10);
                await this.inner.WriteAsync(buffer, offset, count, cancellationToken);
            }
        }
    }
}
