// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class NewLineDelimitedMessageHandlerTests : TestBase
{
    private readonly IReadOnlyList<JsonRpcRequest> mockMessages = new JsonRpcRequest[]
    {
        new JsonRpcRequest { RequestId = new RequestId(1), Method = "a" },
        new JsonRpcRequest { RequestId = new RequestId(2), Method = "b" },
        new JsonRpcRequest { RequestId = new RequestId(3), Method = "c" },
    };

    public NewLineDelimitedMessageHandlerTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void Ctor_Nulls()
    {
        Assert.Throws<ArgumentNullException>("pipe", () => new NewLineDelimitedMessageHandler(null!, new JsonMessageFormatter()));
        Assert.Throws<ArgumentNullException>("formatter", () => new NewLineDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, null!));
        Assert.Throws<ArgumentNullException>("formatter", () => new NewLineDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, null!));
    }

    [Fact]
    public void UTF16_Formatter_NotSupported()
    {
        // Because the handler looks for UTF-8 encoded \n characters, it doesn't support anything else.
        Assert.Throws<NotSupportedException>(() => new NewLineDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, new JsonMessageFormatter(Encoding.Unicode)));
    }

    [Fact]
    public void NewLine()
    {
        var handler = new NewLineDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, new JsonMessageFormatter());

        // Assert default value.
        Assert.Equal(NewLineDelimitedMessageHandler.NewLineStyle.CrLf, handler.NewLine);

        handler.NewLine = NewLineDelimitedMessageHandler.NewLineStyle.Lf;
        Assert.Equal(NewLineDelimitedMessageHandler.NewLineStyle.Lf, handler.NewLine);
    }

    [Fact]
    public async Task Reading_MixedLineEndings()
    {
        var pipe = new Pipe();
        var handler = new NewLineDelimitedMessageHandler(null, pipe.Reader, new JsonMessageFormatter());

        // Send messages with mixed line endings to the handler..
        var testFormatter = new JsonMessageFormatter();
        testFormatter.Serialize(pipe.Writer, this.mockMessages[0]);
        pipe.Writer.Write(testFormatter.Encoding.GetBytes("\n"));
        testFormatter.Serialize(pipe.Writer, this.mockMessages[1]);
        pipe.Writer.Write(testFormatter.Encoding.GetBytes("\r\n"));
        testFormatter.Serialize(pipe.Writer, this.mockMessages[2]);
        pipe.Writer.Write(testFormatter.Encoding.GetBytes("\r\n"));
        await pipe.Writer.FlushAsync(this.TimeoutToken);
        pipe.Writer.Complete();

        // Assert that the handler can read each one.
        var readMessage1 = (JsonRpcRequest?)await handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(this.mockMessages[0].RequestId, readMessage1!.RequestId);
        Assert.Equal(this.mockMessages[0].Method, readMessage1.Method);
        var readMessage2 = (JsonRpcRequest?)await handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(this.mockMessages[1].RequestId, readMessage2!.RequestId);
        Assert.Equal(this.mockMessages[1].Method, readMessage2.Method);
        var readMessage3 = (JsonRpcRequest?)await handler.ReadAsync(this.TimeoutToken);
        Assert.Equal(this.mockMessages[2].RequestId, readMessage3!.RequestId);
        Assert.Equal(this.mockMessages[2].Method, readMessage3.Method);
        Assert.Null(await handler.ReadAsync(this.TimeoutToken));
    }

    [Fact]
    public async Task Reading_IncompleteLine()
    {
        var pipe = new Pipe();
        var handler = new NewLineDelimitedMessageHandler(null, pipe.Reader, new JsonMessageFormatter());
        var testFormatter = new JsonMessageFormatter();

        // Send just the message, but no newline yet.
        testFormatter.Serialize(pipe.Writer, this.mockMessages[0]);
        await pipe.Writer.FlushAsync(this.TimeoutToken);

        // Assert that the handler will wait for more bytes.
        var readTask = handler.ReadAsync(this.TimeoutToken).AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => readTask.WithTimeout(ExpectedTimeout));

        // Now finish with a newline and assert that the message was read.
        pipe.Writer.Write(testFormatter.Encoding.GetBytes("\r\n"));
        await pipe.Writer.FlushAsync(this.TimeoutToken);
        var msg = await readTask.WithCancellation(this.TimeoutToken);
        Assert.True(msg is JsonRpcRequest);
    }

    [Fact]
    public async Task Writing_MixedLineEndings()
    {
        var pipe = new Pipe();
        var handler = new NewLineDelimitedMessageHandler(pipe.Writer, null, new JsonMessageFormatter());

        // Use the handler to write out a couple messages with mixed line endings.
        await handler.WriteAsync(this.mockMessages[0], this.TimeoutToken); // CRLF
        handler.NewLine = NewLineDelimitedMessageHandler.NewLineStyle.Lf;
        await handler.WriteAsync(this.mockMessages[1], this.TimeoutToken); // LF
        await handler.DisposeAsync();

        using var streamReader = new StreamReader(pipe.Reader.AsStream(), handler.Formatter.Encoding);
        string allMessages = await streamReader.ReadToEndAsync();

        // Use CR and LF counts to quickly figure whether our new line styles were honored.
        Assert.Equal(3, allMessages.Split('\n').Length);
        Assert.Equal(2, allMessages.Split('\r').Length);

        // Now actually parse the messages.
        var msgJson = allMessages.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(m => JToken.Parse(m.Trim())).ToArray();
        Assert.Equal(2, msgJson.Length);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(this.mockMessages[i].RequestId.Number, msgJson[i]["id"].Value<int>());
        }
    }
}
