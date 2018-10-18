// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Text;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonMessageFormatterTests : TestBase
{
    public JsonMessageFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void DefaultEncodingLacksPreamble()
    {
        var formatter = new JsonMessageFormatter();
        Assert.Empty(formatter.Encoding.GetPreamble());
    }

    [Fact]
    public void EncodingProperty_UsedToFormat()
    {
        JsonRpcRequest msg = new JsonRpcRequest { Method = "a" };
        var builder = new Sequence<byte>();
        var formatter = new JsonMessageFormatter();

        formatter.Encoding = Encoding.ASCII;
        formatter.Serialize(builder, msg);
        long asciiLength = builder.AsReadOnlySequence.Length;
        var readMsg = (JsonRpcRequest)formatter.Deserialize(builder.AsReadOnlySequence);
        Assert.Equal(msg.Method, readMsg.Method);

        builder.Reset();
        formatter.Encoding = Encoding.UTF32;
        formatter.Serialize(builder, msg);
        long utf32Length = builder.AsReadOnlySequence.Length;
        readMsg = (JsonRpcRequest)formatter.Deserialize(builder.AsReadOnlySequence);
        Assert.Equal(msg.Method, readMsg.Method);

        Assert.Equal(utf32Length - Encoding.UTF32.GetPreamble().Length, asciiLength * 4);
    }
}
