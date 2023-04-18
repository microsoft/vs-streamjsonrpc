// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;
using System.Text;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcSystemTextJsonHeadersTests : JsonRpcTests
{
    public JsonRpcSystemTextJsonHeadersTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers(bool controlledFlushingClient)
    {
        this.clientMessageFormatter = new SystemTextJsonFormatter
        {
        };
        this.serverMessageFormatter = new SystemTextJsonFormatter
        {
        };

        this.serverMessageHandler = new HeaderDelimitedMessageHandler(this.serverStream, this.serverStream, this.serverMessageFormatter);
        this.clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(this.clientStream, this.clientMessageFormatter)
            : new HeaderDelimitedMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
    }

    protected class DelayedFlushingHandler : HeaderDelimitedMessageHandler, IControlledFlushHandler
    {
        public DelayedFlushingHandler(Stream stream, IJsonRpcMessageFormatter formatter)
            : base(stream, formatter)
        {
        }

        public AsyncAutoResetEvent FlushEntered { get; } = new AsyncAutoResetEvent();

        public AsyncManualResetEvent AllowFlushAsyncExit { get; } = new AsyncManualResetEvent();

        protected override async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            this.FlushEntered.Set();
            await this.AllowFlushAsyncExit.WaitAsync();
            await base.FlushAsync(cancellationToken);
        }
    }

    private class StringBase64Converter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String((string)reader.Value!));
            return decoded;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            var stringValue = (string?)value;
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(stringValue!));
            writer.WriteValue(encoded);
        }
    }
}
