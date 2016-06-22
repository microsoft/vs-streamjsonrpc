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
    private readonly JsonRpc clientRpc;

    public MessageHeaderTests(ITestOutputHelper logger)
        : base(logger)
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.clientRpc = JsonRpc.Attach(this.clientStream);

        this.TimeoutToken.Register(this.clientRpc.Dispose);
    }

    [Fact]
    public async Task HeaderEmitted()
    {
        await this.clientRpc.NotifyAsync("someMethod");
        this.clientRpc.Dispose();
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
            bytesRead += this.serverStream.Read(messageBuffer, bytesRead, length - bytesRead);
        }

        // Assert that the stream terminates after the alleged length of the only message sent.
        Assert.Equal(-1, this.serverStream.ReadByte());

        // Decode the message for logging purposes.
        // Actually deserializing the message is beyond the scope of this test.
        Encoding encoding = Encoding.UTF8;
        string message = encoding.GetString(messageBuffer);
        this.Logger.WriteLine(message);
    }
}
