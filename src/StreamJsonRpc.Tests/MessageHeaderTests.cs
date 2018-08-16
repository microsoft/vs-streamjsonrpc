// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        var sr = new StreamReader(this.serverStream, Encoding.ASCII, false);
        var headers = new Dictionary<string, string>();
        string header;
        var headerRegEx = new Regex("(.+?): (.+)");
        while ((header = sr.ReadLine())?.Length > 0)
        {
            this.Logger.WriteLine(header);
            var match = headerRegEx.Match(header);
            Assert.True(match.Success);
            headers[match.Groups[1].Value] = match.Groups[2].Value;
        }

        Assert.True(headers.ContainsKey("Content-Length"));
        Assert.True(int.TryParse(headers["Content-Length"], out int length));
        Assert.NotEqual(0, length);
        byte[] messageBuffer = new byte[length];
        int bytesRead = 0;
        while (bytesRead < length)
        {
            bytesRead += await this.serverStream.ReadAsync(messageBuffer, bytesRead, length - bytesRead, this.TimeoutToken);
        }

        // Assert that the stream terminates after the alleged length of the only message sent.
        Assert.Equal(-1, this.serverStream.ReadByte());

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
        string jsonMessage = @"{""method"":""Foo"",""id"":1}";
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
        await rpcClient.NotifyAsync("Foo");
        rpcClient.Dispose();

        var reader = new StreamReader(this.serverStream, Encoding.ASCII);
        var headerLines = new List<string>();
        string line;
        while ((line = reader.ReadLine()) != string.Empty)
        {
            headerLines.Add(line);
        }

        this.Logger.WriteLine(string.Join(Environment.NewLine, headerLines));

        // utf-8 headers may not be present because they are the default, per the protocol spec.
        if (encodingName != "utf-8")
        {
            Assert.Contains(headerLines, l => l.Contains($"charset={encodingName}"));
        }

        reader = new StreamReader(this.serverStream, Encoding.GetEncoding(encodingName));
        string json = reader.ReadToEnd();
        Assert.Equal('{', json[0]);
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
