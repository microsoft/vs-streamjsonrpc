// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics;
using System.Text;
using MessagePack;
using Nerdbank.Streams;
using PolyType.ReflectionProvider;

#pragma warning disable PolyTypeJson

public class NullParamsTests
{
    private static readonly byte[] JsonRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"test","params":null}""");

    private static readonly byte[] MessagePackRequest = CreateMessagePackRequest();

    public static IEnumerable<object[]> Formatters
    {
        get
        {
#pragma warning disable CS0618 // The tests cover behavior across both legacy and current formatters.
            yield return [new JsonMessageFormatter()];
            yield return [new MessagePackFormatter()];
#pragma warning restore CS0618
            yield return [new SystemTextJsonFormatter()];
            yield return [new PolyTypeJsonFormatter { TypeShapeProvider = ReflectionTypeShapeProvider.Default }];
            yield return [new NerdbankMessagePackFormatter { TypeShapeProvider = ReflectionTypeShapeProvider.Default }];
        }
    }

    [Theory]
    [MemberData(nameof(Formatters))]
    public void NullParamsAreAcceptedAndWarnedAboutOnce(IJsonRpcMessageFormatter formatter)
    {
#pragma warning disable CS0618 // The tests cover behavior across both legacy and current formatters.
        byte[] request = formatter is MessagePackFormatter or NerdbankMessagePackFormatter ? MessagePackRequest : JsonRequest;
#pragma warning restore CS0618
        var listener = new CollectingTraceListener();
        using var jsonRpc = new JsonRpc(new HeaderDelimitedMessageHandler(Stream.Null, formatter))
        {
            TraceSource = new TraceSource(formatter.GetType().Name, SourceLevels.Warning)
            {
                Listeners = { listener },
            },
        };

        JsonRpcRequest firstRequest = Assert.IsAssignableFrom<JsonRpcRequest>(formatter.Deserialize(new ReadOnlySequence<byte>(request)));
        JsonRpcRequest secondRequest = Assert.IsAssignableFrom<JsonRpcRequest>(formatter.Deserialize(new ReadOnlySequence<byte>(request)));

        Assert.Equal(0, firstRequest.ArgumentCount);
        Assert.Equal(0, secondRequest.ArgumentCount);
        (TraceEventType EventType, string? Message) warning = Assert.Single(
            listener.Events,
            e => e.EventType == TraceEventType.Warning && e.Message?.Contains("\"params\"", StringComparison.Ordinal) is true);
        Assert.Contains("remote party", warning.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateMessagePackRequest()
    {
        using var buffer = new Sequence<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(4);
        writer.Write("jsonrpc");
        writer.Write("2.0");
        writer.Write("id");
        writer.Write(1);
        writer.Write("method");
        writer.Write("test");
        writer.Write("params");
        writer.WriteNil();
        writer.Flush();
        return buffer.AsReadOnlySequence.ToArray();
    }
}
