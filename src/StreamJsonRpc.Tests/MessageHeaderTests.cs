// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class MessageHeaderTests : TestBase
{
    private readonly Stream clientStream;
    private readonly Stream serverStream;

    public MessageHeaderTests(ITestOutputHelper logger)
        : base(logger)
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;
    }

    public static IEnumerable<object[]> TestedEncodings => new[]
    {
        new[] { "utf-8" },
        new[] { "utf-16" },
    };

    [Fact]
    public async Task HeaderEmitted()
    {
        var clientRpc = JsonRpc.Attach(this.clientStream);
        this.TimeoutToken.Register(clientRpc.Dispose);

        await clientRpc.NotifyAsync("someMethod");
        clientRpc.Dispose();
        MemoryStream seekableServerStream = await this.GetSeekableServerStream();
        var sr = new StreamReader(seekableServerStream, Encoding.ASCII, false);
        var headers = new Dictionary<string, string>();
        string? header;
        var headerRegEx = new Regex("(.+?): (.+)");
        int bytesRead = 0;
        while ((header = sr.ReadLine())?.Length > 0)
        {
            bytesRead += header.Length + 2; // CRLF
            this.Logger.WriteLine(header);
            var match = headerRegEx.Match(header);
            Assert.True(match.Success);
            headers[match.Groups[1].Value] = match.Groups[2].Value;
        }

        bytesRead += 2; // final CRLF

        Assert.True(headers.ContainsKey("Content-Length"));
        Assert.True(int.TryParse(headers["Content-Length"], out int length));
        Assert.NotEqual(0, length);
        byte[] messageBuffer = new byte[length];
        seekableServerStream.Position = bytesRead; // reset position to counteract buffer built up in StreamReader
        bytesRead = 0;
        while (bytesRead < length)
        {
            int bytesJustRead = await seekableServerStream.ReadAsync(messageBuffer, bytesRead, length - bytesRead, this.TimeoutToken);
            Assert.NotEqual(0, bytesJustRead); // end of stream reached unexpectedly.
            bytesRead += bytesJustRead;
        }

        // Assert that the stream terminates after the alleged length of the only message sent.
        Assert.Equal(-1, seekableServerStream.ReadByte());

        // Decode the message for logging purposes.
        // Actually deserializing the message is beyond the scope of this test.
        Encoding encoding = Encoding.UTF8;
        string message = encoding.GetString(messageBuffer);
        this.Logger.WriteLine(message);
    }

    [Theory]
    [MemberData(nameof(TestedEncodings))]
    public async Task ReceiveMessageWithEncoding(string encodingName)
    {
        var server = new Server();
        var rpcServer = JsonRpc.Attach(this.serverStream, server);

        Encoding contentEncoding = Encoding.GetEncoding(encodingName);
        string jsonMessage = @"{""jsonrpc"":""2.0"",""method"":""Foo"",""id"":1}";
        byte[] message = contentEncoding.GetBytes(jsonMessage);

        // Write the header, which is always in ASCII.
        Encoding headerEncoding = Encoding.ASCII;
        var headerBuffer = new MemoryStream();
        string header = $"Content-Length: {message.Length}\r\nContent-Type: text/plain; charset={encodingName}\r\n\r\n";
        byte[] headerBytes = headerEncoding.GetBytes(header);
        await this.clientStream.WriteAsync(headerBytes, 0, headerBytes.Length, this.TimeoutToken);
        await this.clientStream.WriteAsync(message, 0, message.Length, this.TimeoutToken);

        // Wait for response.
        byte[] receiveBuffer = new byte[1];
        await this.clientStream.ReadAsync(receiveBuffer, 0, 1, this.TimeoutToken); // just wait for the response to start.

        Assert.Equal(1, server.FooCalledCount);
    }

    [Theory]
    [MemberData(nameof(TestedEncodings))]
    public async Task SendMessageWithEncoding(string encodingName)
    {
        var messageHandler = new HeaderDelimitedMessageHandler(this.clientStream, this.clientStream);
        var rpcClient = new JsonRpc(messageHandler);
        messageHandler.Encoding = Encoding.GetEncoding(encodingName);
        await rpcClient.NotifyAsync("Foo").WithCancellation(this.TimeoutToken);
        rpcClient.Dispose();

        MemoryStream seekableServerStream = await this.GetSeekableServerStream();
        int bytesRead = 0;
        var reader = new StreamReader(seekableServerStream, Encoding.ASCII);
        var headerLines = new List<string>();
        string line;
        while ((line = await reader.ReadLineAsync().WithCancellation(this.TimeoutToken)) != string.Empty)
        {
            headerLines.Add(line);
            bytesRead += line.Length + 2; // + CRLF
        }

        bytesRead += 2; // final CRLF
        this.Logger.WriteLine(string.Join(Environment.NewLine, headerLines));

        // utf-8 headers may not be present because they are the default, per the protocol spec.
        if (encodingName != "utf-8")
        {
            Assert.Contains(headerLines, l => l.Contains($"charset={encodingName}"));
        }

        // Because the first StreamReader probably read farther (to fill its buffer) than the end of the headers,
        // we need to reposition the stream at the start of the content to create a new StreamReader.
        seekableServerStream.Position = bytesRead;
        reader = new StreamReader(seekableServerStream, Encoding.GetEncoding(encodingName));
        string json = await reader.ReadToEndAsync().WithCancellation(this.TimeoutToken);
        Assert.Equal('{', json[0]);
    }

    private async Task<MemoryStream> GetSeekableServerStream()
    {
        var seekableServerStream = new MemoryStream();
        await this.serverStream.CopyToAsync(seekableServerStream, 4096, this.TimeoutToken);
        seekableServerStream.Position = 0;
        return seekableServerStream;
    }

    private class Server
    {
        internal int FooCalledCount { get; private set; }

        public void Foo()
        {
            this.FooCalledCount++;
        }
    }
}
