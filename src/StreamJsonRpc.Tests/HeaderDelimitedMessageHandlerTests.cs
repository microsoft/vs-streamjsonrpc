// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Text;
using System.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class HeaderDelimitedMessageHandlerTests : TestBase
{
    private const string CRLF = "\r\n";
    private readonly MemoryStream sendingStream = new MemoryStream();
    private readonly MemoryStream receivingStream = new MemoryStream();
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
}
