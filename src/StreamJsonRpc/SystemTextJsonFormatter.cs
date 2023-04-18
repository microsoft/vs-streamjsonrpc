// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// A formatter that emits UTF-8 encoded JSON where user data should be serializable via the <see cref="JsonSerializer"/>.
/// </summary>
public partial class SystemTextJsonFormatter : IJsonRpcMessageFormatter, IJsonRpcMessageTextFormatter
{
    private static readonly JsonWriterOptions WriterOptions = new() { };

    private static readonly JsonReaderOptions ReaderOptions = new() { };

    private static readonly JsonDocumentOptions DocumentOptions = new() { };

    /// <summary>
    /// UTF-8 encoding without a preamble.
    /// </summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc/>
    public Encoding Encoding
    {
        get => DefaultEncoding;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets or sets the options to use when serializing and deserializing JSON containing user data.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new();

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => this.Deserialize(contentBuffer, this.Encoding);

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
    {
        if (encoding is not UTF8Encoding)
        {
            throw new NotSupportedException("Only our default encoding is supported.");
        }

        JsonDocument document = JsonDocument.Parse(contentBuffer, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object at the root of the message.");
        }

        JsonRpcMessage returnValue;
        if (document.RootElement.TryGetProperty(Utf8Strings.method, out JsonElement methodElement))
        {
            JsonRpcRequest request = new(this)
            {
                RequestId = ReadRequestId(),
                Method = methodElement.GetString(),
                JsonArguments = document.RootElement.TryGetProperty(Utf8Strings.@params, out JsonElement paramsElement) ? paramsElement : null,
            };
            returnValue = request;
        }
        else if (document.RootElement.TryGetProperty(Utf8Strings.result, out JsonElement resultElement))
        {
            JsonRpcResult result = new(this)
            {
                RequestId = ReadRequestId(),
                JsonResult = resultElement,
            };
            returnValue = result;
        }
        else if (document.RootElement.TryGetProperty(Utf8Strings.error, out JsonElement errorElement))
        {
            JsonRpcError error = new()
            {
                RequestId = ReadRequestId(),
                Error = new JsonRpcError.ErrorDetail
                {
                    Code = (JsonRpcErrorCode)errorElement.GetProperty(Utf8Strings.code).GetInt64(),
                    Message = errorElement.GetProperty(Utf8Strings.message).GetString(),
                },
            };

            returnValue = error;
        }
        else
        {
            throw new JsonException("Expected a request, result, or error message.");
        }

        if (document.RootElement.TryGetProperty(Utf8Strings.jsonrpc, out JsonElement jsonRpcElement))
        {
            returnValue.Version = jsonRpcElement.ValueEquals(Utf8Strings.v2_0) ? "2.0" : (jsonRpcElement.GetString() ?? throw new JsonException("Unexpected null value for jsonrpc property."));
        }
        else
        {
            // Version 1.0 is implied when it is absent.
            returnValue.Version = "1.0";
        }

        RequestId ReadRequestId()
        {
            if (document.RootElement.TryGetProperty(Utf8Strings.id, out JsonElement idElement))
            {
                return idElement.ValueKind switch
                {
                    JsonValueKind.Number => new RequestId(idElement.GetInt64()),
                    JsonValueKind.String => new RequestId(idElement.GetString()),
                    JsonValueKind.Null => new RequestId(null),
                    _ => throw new JsonException("Unexpected value kind for id property: " + idElement.ValueKind),
                };
            }
            else
            {
                return RequestId.NotSpecified;
            }
        }

        return returnValue;
    }

    /// <inheritdoc/>
    public object GetJsonText(JsonRpcMessage message)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
    {
        using Utf8JsonWriter writer = new(bufferWriter, WriterOptions);
        writer.WriteStartObject();
        WriteVersion();
        switch (message)
        {
            case Protocol.JsonRpcRequest request:
                WriteId(request.RequestId);
                writer.WriteString(Utf8Strings.method, request.Method);
                WriteArguments(request);
                writer.WriteEndObject();
                break;
            case Protocol.JsonRpcResult result:
                WriteId(result.RequestId);
                WriteResult(result);
                writer.WriteEndObject();
                break;
            case JsonRpcError error:
                WriteId(error.RequestId);
                WriteError(error);
                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentException("Unknown message type: " + message.GetType().Name, nameof(message));
        }

        void WriteVersion()
        {
            switch (message.Version)
            {
                case "1.0":
                    // The 1.0 protocol didn't include the version property at all.
                    break;
                case "2.0":
                    writer.WriteString(Utf8Strings.jsonrpc, Utf8Strings.v2_0);
                    break;
                default:
                    writer.WriteString(Utf8Strings.jsonrpc, message.Version);
                    break;
            }
        }

        void WriteId(RequestId requestId)
        {
            if (requestId.Number is long idNumber)
            {
                writer.WriteNumber(Utf8Strings.id, idNumber);
            }
            else if (requestId.String is string idString)
            {
                writer.WriteString(Utf8Strings.id, idString);
            }
            else
            {
                writer.WriteNull(Utf8Strings.id);
            }
        }

        void WriteArguments(Protocol.JsonRpcRequest request)
        {
            if (request.ArgumentsList is not null)
            {
                writer.WriteStartArray(Utf8Strings.@params);
                for (int i = 0; i < request.ArgumentsList.Count; i++)
                {
                    WriteUserData(request.ArgumentsList[i]);
                }

                writer.WriteEndArray();
            }
            else if (request.NamedArguments is not null)
            {
                writer.WriteStartObject(Utf8Strings.@params);
                foreach (KeyValuePair<string, object?> argument in request.NamedArguments)
                {
                    writer.WritePropertyName(argument.Key);
                    WriteUserData(argument.Value);
                }

                writer.WriteEndObject();
            }
        }

        void WriteResult(Protocol.JsonRpcResult result)
        {
            writer.WritePropertyName(Utf8Strings.result);
            WriteUserData(result.Result);
        }

        void WriteError(JsonRpcError error)
        {
            if (error.Error is null)
            {
                throw new ArgumentException($"{nameof(error.Error)} property must be set.", nameof(message));
            }

            writer.WriteStartObject(Utf8Strings.error);
            writer.WriteNumber(Utf8Strings.code, (int)error.Error.Code);
            writer.WriteString(Utf8Strings.message, error.Error.Message);
            if (error.Error.Data is not null)
            {
                writer.WritePropertyName(Utf8Strings.data);
                WriteUserData(error.Error.Data);
            }

            writer.WriteEndObject();
        }

        void WriteUserData(object? value)
        {
            JsonSerializer.Serialize(writer, value, this.JsonSerializerOptions);
        }
    }

    private static class Utf8Strings
    {
#pragma warning disable SA1300 // Element should begin with upper-case letter
        internal static ReadOnlySpan<byte> jsonrpc => "jsonrpc"u8;

        internal static ReadOnlySpan<byte> v2_0 => "2.0"u8;

        internal static ReadOnlySpan<byte> id => "id"u8;

        internal static ReadOnlySpan<byte> method => "method"u8;

        internal static ReadOnlySpan<byte> @params => "params"u8;

        internal static ReadOnlySpan<byte> result => "result"u8;

        internal static ReadOnlySpan<byte> error => "error"u8;

        internal static ReadOnlySpan<byte> code => "code"u8;

        internal static ReadOnlySpan<byte> message => "message"u8;

        internal static ReadOnlySpan<byte> data => "data"u8;
#pragma warning restore SA1300 // Element should begin with upper-case letter
    }

    private class JsonRpcRequest : Protocol.JsonRpcRequest, IJsonRpcMessageBufferManager
    {
        private readonly SystemTextJsonFormatter formatter;

        internal JsonRpcRequest(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        internal JsonElement? JsonArguments { get; set; }

        public void DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);

            // Clear references to buffers that we are no longer entitled to.
            this.JsonArguments = null;
        }

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            JsonElement? valueElement = null;
            switch (this.JsonArguments?.ValueKind)
            {
                case JsonValueKind.Object when name is not null:
                    if (this.JsonArguments.Value.TryGetProperty(name, out JsonElement propertyValue))
                    {
                        valueElement = propertyValue;
                    }

                    break;
                case JsonValueKind.Array:
                    int elementIndex = 0;
                    foreach (JsonElement arrayElement in this.JsonArguments.Value.EnumerateArray())
                    {
                        if (elementIndex++ == position)
                        {
                            valueElement = arrayElement;
                            break;
                        }
                    }

                    break;
                default:
                    throw new JsonException("Unexpected value kind for arguments: " + this.JsonArguments?.ValueKind ?? "null");
            }

            value = valueElement?.Deserialize(typeHint ?? typeof(object), this.formatter.JsonSerializerOptions);
            return valueElement.HasValue;
        }
    }

    private class JsonRpcResult : Protocol.JsonRpcResult, IJsonRpcMessageBufferManager
    {
        private readonly SystemTextJsonFormatter formatter;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        internal JsonElement? JsonResult { get; set; }

        public void DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);

            // Clear references to buffers that we are no longer entitled to.
            this.JsonResult = null;
        }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            return this.JsonResult is null
                ? (T)this.Result!
                : this.JsonResult.Value.Deserialize<T>(this.formatter.JsonSerializerOptions)!;
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(this.JsonResult is not null, "Result is no longer available or has already been deserialized.");

            try
            {
                this.Result = this.JsonResult.Value.Deserialize(resultType, this.formatter.JsonSerializerOptions);
                this.JsonResult = default;
            }
            catch (Exception ex)
            {
                // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                this.resultDeserializationException = ex;
            }
        }
    }
}
