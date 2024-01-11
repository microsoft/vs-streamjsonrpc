// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using Newtonsoft.Json;

public class JsonRpcJsonHeadersTypeHandlingTests : JsonRpcJsonHeadersTests
{
    public JsonRpcJsonHeadersTypeHandlingTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers(
        Stream serverStream,
        Stream clientStream,
        out IJsonRpcMessageFormatter serverMessageFormatter,
        out IJsonRpcMessageFormatter clientMessageFormatter,
        out IJsonRpcMessageHandler serverMessageHandler,
        out IJsonRpcMessageHandler clientMessageHandler,
        bool controlledFlushingClient)
    {
        serverMessageFormatter = new JsonMessageFormatter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            JsonSerializer =
            {
                TypeNameHandling = TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Converters =
                {
                    new UnserializableTypeConverter(),
                    new TypeThrowsWhenDeserializedConverter(),
                },
            },
        };

        clientMessageFormatter = new JsonMessageFormatter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            JsonSerializer =
            {
                TypeNameHandling = TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Converters =
                {
                    new UnserializableTypeConverter(),
                    new TypeThrowsWhenDeserializedConverter(),
                },
            },
        };

        serverMessageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, serverMessageFormatter);
        clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(clientStream, clientMessageFormatter)
            : new HeaderDelimitedMessageHandler(clientStream, clientStream, clientMessageFormatter);
    }
}
