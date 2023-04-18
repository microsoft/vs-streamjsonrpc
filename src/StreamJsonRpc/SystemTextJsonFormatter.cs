// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// A formatter that emits UTF-8 encoded JSON where user data should be serializable via the <see cref="JsonSerializer"/>.
/// </summary>
public partial class SystemTextJsonFormatter : IJsonRpcMessageFormatter, IJsonRpcMessageTextFormatter
{
    private static readonly JsonWriterOptions WriterOptions = new() { };

    private static readonly JsonDocumentOptions DocumentOptions = new() { };

    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> to use for the envelope and built-in types.
    /// </summary>
    private static readonly JsonSerializerOptions BuiltInSerializerOptions = new()
    {
        Converters =
        {
            RequestIdJsonConverter.Instance,
        },
    };

    /// <summary>
    /// UTF-8 encoding without a preamble.
    /// </summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private JsonSerializerOptions massagedUserDataSerializerOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemTextJsonFormatter"/> class.
    /// </summary>
    public SystemTextJsonFormatter()
    {
        this.massagedUserDataSerializerOptions = this.MassageUserDataSerializerOptions(new());
    }

    /// <inheritdoc/>
    public Encoding Encoding
    {
        get => DefaultEncoding;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Sets the options to use when serializing and deserializing JSON containing user data.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions
    {
        set => this.massagedUserDataSerializerOptions = this.MassageUserDataSerializerOptions(new(value));
    }

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
                Error = new JsonRpcError.ErrorDetail(this)
                {
                    Code = (JsonRpcErrorCode)errorElement.GetProperty(Utf8Strings.code).GetInt64(),
                    Message = errorElement.GetProperty(Utf8Strings.message).GetString(),
                    JsonData = errorElement.TryGetProperty(Utf8Strings.data, out JsonElement dataElement) ? dataElement : null,
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
            return document.RootElement.TryGetProperty(Utf8Strings.id, out JsonElement idElement)
                ? idElement.Deserialize<RequestId>(BuiltInSerializerOptions)
                : RequestId.NotSpecified;
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
            case Protocol.JsonRpcError error:
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

        void WriteId(RequestId id)
        {
            if (!id.IsEmpty)
            {
                writer.WritePropertyName(Utf8Strings.id);
                RequestIdJsonConverter.Instance.Write(writer, id, BuiltInSerializerOptions);
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

        void WriteError(Protocol.JsonRpcError error)
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
            JsonSerializer.Serialize(writer, value, this.massagedUserDataSerializerOptions);
        }
    }

    private JsonSerializerOptions MassageUserDataSerializerOptions(JsonSerializerOptions options)
    {
        options.Converters.Add(RequestIdJsonConverter.Instance);
        return options;
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

        private int argumentCount;

        private JsonElement? jsonArguments;

        internal JsonRpcRequest(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override int ArgumentCount => this.argumentCount;

        internal JsonElement? JsonArguments
        {
            get => this.jsonArguments;
            init
            {
                this.jsonArguments = value;
                if (value.HasValue)
                {
                    this.argumentCount = CountArguments(value.Value);
                }
            }
        }

        public void DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);

            // Clear references to buffers that we are no longer entitled to.
            this.jsonArguments = null;
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

            try
            {
                value = valueElement?.Deserialize(typeHint ?? typeof(object), this.formatter.massagedUserDataSerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
            }

            return valueElement.HasValue;
        }

        private static int CountArguments(JsonElement arguments)
        {
            int count = 0;
            switch (arguments.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (JsonElement element in arguments.EnumerateArray())
                    {
                        count++;
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in arguments.EnumerateObject())
                    {
                        count++;
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unexpected value kind: " + arguments.ValueKind);
            }

            return count;
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
                : this.JsonResult.Value.Deserialize<T>(this.formatter.massagedUserDataSerializerOptions)!;
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(this.JsonResult is not null, "Result is no longer available or has already been deserialized.");

            try
            {
                this.Result = this.JsonResult.Value.Deserialize(resultType, this.formatter.massagedUserDataSerializerOptions);
                this.JsonResult = default;
            }
            catch (Exception ex)
            {
                // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                this.resultDeserializationException = ex;
            }
        }
    }

    private class JsonRpcError : Protocol.JsonRpcError, IJsonRpcMessageBufferManager
    {
        internal new ErrorDetail? Error
        {
            get => (ErrorDetail?)base.Error;
            set => base.Error = value;
        }

        public void DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);

            // Clear references to buffers that we are no longer entitled to.
            if (this.Error is { } detail)
            {
                detail.JsonData = null;
            }
        }

        internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
        {
            private readonly SystemTextJsonFormatter formatter;

            internal ErrorDetail(SystemTextJsonFormatter formatter)
            {
                this.formatter = formatter;
            }

            internal JsonElement? JsonData { get; set; }

            public override object? GetData(Type dataType)
            {
                Requires.NotNull(dataType, nameof(dataType));
                if (this.JsonData is null)
                {
                    return this.Data;
                }

                try
                {
                    return this.JsonData.Value.Deserialize(dataType, this.formatter.massagedUserDataSerializerOptions);
                }
                catch (JsonException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        return this.JsonData.Value.Deserialize<object>(this.formatter.massagedUserDataSerializerOptions);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }

            protected internal override void SetExpectedDataType(Type dataType)
            {
                this.Data = this.GetData(dataType);

                // Clear the source now that we've deserialized to prevent GetData from attempting
                // deserialization later when the buffer may be recycled on another thread.
                this.JsonData = default;
            }
        }
    }

    private class RequestIdJsonConverter : JsonConverter<RequestId>
    {
        internal static readonly RequestIdJsonConverter Instance = new();

        public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => new RequestId(reader.GetInt64()),
                JsonTokenType.String => new RequestId(reader.GetString()),
                JsonTokenType.Null => RequestId.Null,
                _ => throw new JsonException("Unexpected token type for id property: " + reader.TokenType),
            };
        }

        public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
        {
            if (value.Number is long idNumber)
            {
                writer.WriteNumberValue(idNumber);
            }
            else if (value.String is string idString)
            {
                writer.WriteStringValue(idString);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
