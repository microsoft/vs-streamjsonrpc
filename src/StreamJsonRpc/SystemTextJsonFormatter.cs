// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text;
using System.Text.Json;
using StreamJsonRpc.Protocol;

namespace StreamJsonRpc;

public class SystemTextJsonFormatter : IJsonRpcMessageFormatter, IJsonRpcMessageTextFormatter
{
    private static JsonWriterOptions WriterOptions = new();
    private static JsonReaderOptions ReaderOptions = new();

    private static JsonSerializerOptions SerializerOptions = new();

    /// <summary>
    /// UTF-8 encoding without a preamble.
    /// </summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public Encoding Encoding
    {
        get => DefaultEncoding;
        set => throw new NotSupportedException();
    }

    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
    {
        throw new NotImplementedException();
    }

    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
    {
        if (encoding is not UTF8Encoding)
        {
            throw new NotSupportedException("Only our default encoding is supported.");
        }

        Utf8JsonReader reader = new(contentBuffer, ReaderOptions);
        return JsonSerializer.Deserialize<JsonRpcRequest>(ref reader, SerializerOptions) ?? throw new Exception("Empty message");
    }

    public object GetJsonText(JsonRpcMessage message)
    {
        throw new NotImplementedException();
    }

    public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
    {
        using Utf8JsonWriter writer = new(bufferWriter, WriterOptions);
        JsonSerializer.Serialize(writer, message, SerializerOptions);
    }
}
