// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
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
        // Use strict pipe writer so we get deterministic writes for consistent testing.
        this.handler = new HeaderDelimitedMessageHandler(this.sendingStream.UseStrictPipeWriter(), this.receivingStream.UseStrictPipeReader(), new JsonMessageFormatter());
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
        this.handler = new HeaderDelimitedMessageHandler(this.sendingStream.UseStrictPipeWriter(), this.receivingStream.UseStrictPipeReader(), new MockFormatter());
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
}
