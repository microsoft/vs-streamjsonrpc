using StreamJsonRpc;
using System.IO;
using System.Text;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

public class HeaderDelimitedMessageHandlerTests : TestBase
{
    private readonly MemoryStream sendingStream = new MemoryStream();
    private readonly MemoryStream receivingStream = new MemoryStream();
    private HeaderDelimitedMessageHandler handler;

    public HeaderDelimitedMessageHandlerTests(ITestOutputHelper logger) : base(logger)
    {
        this.handler = new HeaderDelimitedMessageHandler(this.sendingStream, this.receivingStream);
    }

    [Fact]
    public void ReadCoreAsync_HandlesSpacingCorrectly()
    {
        string content =
@"Content-Length:  10   
Content-Type: application/vscode-jsonrpc;charset=utf-8

0123456789";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        string readContent = this.handler.ReadAsync(default(CancellationToken)).GetAwaiter().GetResult();
        Assert.Equal<string>("0123456789", readContent);
        
        this.receivingStream.Position = 0;
        this.receivingStream.SetLength(0);

        content =
@"Content-Length:5

ABCDE";
        bytes = Encoding.UTF8.GetBytes(content);
        this.receivingStream.Write(bytes, 0, bytes.Length);
        this.receivingStream.Flush();
        this.receivingStream.Position = 0;

        readContent = this.handler.ReadAsync(default(CancellationToken)).GetAwaiter().GetResult();
        Assert.Equal<string>("ABCDE", readContent);
    }
}
