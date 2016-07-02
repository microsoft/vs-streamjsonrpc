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
        int length;
        Assert.True(int.TryParse(headers["Content-Length"], out length));
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
    [InlineData("utf-8")]
    [InlineData("utf-16")]
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

    private class Server
    {
        internal int FooCalledCount { get; private set; }

        public void Foo()
        {
            this.FooCalledCount++;
        }
    }
}
