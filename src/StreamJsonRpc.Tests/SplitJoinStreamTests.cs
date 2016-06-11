using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using Xunit;

public class SplitJoinStreamTests
{
    private const string DefaultDelimiter = "\0";

    [Fact]
    public async Task CanReadTrailing()
    {
        const string delimiter = "\r\n";
        await this.RunReadTest(
            new SplitJoinStreamOptions { Delimiter = delimiter, ReadTrailing = true },
            "foo" + delimiter + "bar" + delimiter + delimiter + delimiter + "bazz",
            "foo", "bar", "bazz");
    }

    [Fact]
    public async Task CanReadTrailingWithoutDelimiters()
    {
        const string delimiter = "|";
        await this.RunReadTest(
            new SplitJoinStreamOptions { Delimiter = delimiter, ReadTrailing = true },
            "foo",
            "foo");
    }

    [Fact]
    public async Task CanReadWithLongDelimiters()
    {
        var delimiter = new string('a', 4200);
        await this.RunReadTest(
            new SplitJoinStreamOptions { Delimiter = delimiter, ReadTrailing = true },
            "foo" + delimiter + "bar" + delimiter + "bazz",
            "foo", "bar", "bazz");
    }

    [Fact]
    public async Task CanReadMultibyteCharAcrossBufferBoundary()
    {
        var str = 'a' + new string('Ж', SplitJoinStream.BufferSize / 2);
        Assert.Equal(Encoding.UTF8.GetByteCount(str), SplitJoinStream.BufferSize + 1);
        await this.RunReadTest(
            new SplitJoinStreamOptions(),
            str + DefaultDelimiter + str + DefaultDelimiter,
            str, str);
    }

    [Fact]
    public async Task CanReadMultibyteDelimiterAcrossBufferBoundary()
    {
        const string delimiter = "Ж";
        var str = new string(' ', SplitJoinStream.BufferSize - 1);
        Assert.Equal(Encoding.UTF8.GetByteCount(delimiter), 2);
        Assert.Equal(Encoding.UTF8.GetByteCount(str + delimiter), SplitJoinStream.BufferSize + 1);

        await this.RunReadTest(
            new SplitJoinStreamOptions { Delimiter = delimiter },
            str + delimiter + str + delimiter,
            str, str);
    }

    [Fact]
    public async Task CanReadSkippingTrailing()
    {
        await this.RunReadTest(
            new SplitJoinStreamOptions(),
            DefaultDelimiter + "foo" + DefaultDelimiter + "bar" + DefaultDelimiter + "bazz",
            "foo", "bar");
    }

    [Fact]
    public async Task CanReadSkippingTrailingWithoutDelimiters()
    {
        await this.RunReadTest(new SplitJoinStreamOptions(), "foo" /* Nothing to read */);
    }

    [Fact]
    public async Task CanReadLongString()
    {
        var longString = new string('a', 4200);
        await this.RunReadTest(
            new SplitJoinStreamOptions(),
            longString + DefaultDelimiter + longString + DefaultDelimiter + DefaultDelimiter + longString,
            longString, longString);
    }

    [Fact]
    public async Task CanReadFromStreamWithOnlyDelimiters()
    {
        await this.RunReadTest(new SplitJoinStreamOptions(), DefaultDelimiter + DefaultDelimiter + DefaultDelimiter /* Nothing to read */);
    }

    [Fact]
    public async Task CanReadFromEmptyStream()
    {
        await this.RunReadTest(new SplitJoinStreamOptions {}, "" /* Nothing to read */);
    }

    [Fact]
    public async Task CanReadUTF32()
    {
        await this.RunReadTest(
            new SplitJoinStreamOptions { Encoding = Encoding.UTF32 },
            "foo" + DefaultDelimiter + "bar" + DefaultDelimiter + DefaultDelimiter + DefaultDelimiter + "bazz",
            "foo", "bar");
    }

    [Fact]
    public async Task CanCancelRead()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        using (var split = new SplitJoinStream(new SplitJoinStreamOptions { Readable = new MemoryStream(new byte[] { 32, 46 }) }))
        {
            await Assert.ThrowsAsync(typeof(TaskCanceledException), async () => await split.ReadAsync(cts.Token));
        }
    }

    [Fact]
    public void CanDisposeStreamsByDefault()
    {
        var options = new SplitJoinStreamOptions
        {
            Readable = new MemoryStream(new byte[] { 32, 46 }),
            Writable = new MemoryStream(new byte[] { 55, 45 }),
            // LeaveOpen = false, - this is the default
        };

        using (var split = new SplitJoinStream(options)) {}
        Assert.False(options.Writable.CanWrite);
        Assert.False(options.Readable.CanWrite);
    }

    [Fact]
    public void CanLeaveStreamsOpen()
    {
        var options = new SplitJoinStreamOptions
        {
            Readable = new MemoryStream(new byte[] { 32, 46 }),
            Writable = new MemoryStream(new byte[] { 55, 45 }),
            LeaveOpen = true,
        };

        using (var split = new SplitJoinStream(options)) { }
        Assert.True(options.Writable.CanWrite);
        Assert.True(options.Readable.CanWrite);
    }

    [Fact]
    public async Task CanWrite()
    {
        const string StringToWrite = "foo";
        using (var writable = new MemoryStream())
        {
            using (var split = new SplitJoinStream(new SplitJoinStreamOptions { Writable = writable, LeaveOpen = true }))
            {
                await split.WriteAsync(StringToWrite);
            }

            writable.Position = 0;
            using (var reader = new StreamReader(writable))
            {
                string actual = reader.ReadToEnd();
                Assert.Equal(StringToWrite + DefaultDelimiter, actual);
            }
        }
    }

    [Fact]
    public async Task CanCancelWrite()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        using (var split = new SplitJoinStream(new SplitJoinStreamOptions { Writable = new MemoryStream() }))
        {
            await Assert.ThrowsAsync(typeof(TaskCanceledException), async () => await split.WriteAsync("foo", cts.Token));
        }
    }

    private async Task RunReadTest(SplitJoinStreamOptions options, string input, params string[] expectedReads)
    {
        var encoding = options.Encoding ?? Encoding.UTF8;
        options.Readable = new MemoryStream(encoding.GetBytes(input));
        using (var split = new SplitJoinStream(options))
        {
            foreach (string expectedRead in expectedReads)
            {
                string message = await split.ReadAsync();
                Assert.Equal(expectedRead, message);
            }

            string messageAfterStreamEnds = await split.ReadAsync();
            Assert.Null(messageAfterStreamEnds);
        }
    }
}
