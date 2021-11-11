// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcJsonHeadersTypeHandlingTests : JsonRpcJsonHeadersTests
{
    public JsonRpcJsonHeadersTypeHandlingTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers(bool controlledFlushingClient)
    {
        this.serverMessageFormatter = new JsonMessageFormatter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            JsonSerializer =
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Converters =
                {
                    new UnserializableTypeConverter(),
                    new TypeThrowsWhenDeserializedConverter(),
                },
            },
        };

        this.clientMessageFormatter = new JsonMessageFormatter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            JsonSerializer =
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Converters =
                {
                    new UnserializableTypeConverter(),
                    new TypeThrowsWhenDeserializedConverter(),
                },
            },
        };

        this.serverMessageHandler = new HeaderDelimitedMessageHandler(this.serverStream, this.serverStream, this.serverMessageFormatter);
        this.clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(this.clientStream, this.clientMessageFormatter)
            : new HeaderDelimitedMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
    }
}
