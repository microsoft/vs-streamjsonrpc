// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit.Abstractions;

public class CustomCancellationStrategyJsonTypeHandlingTests : CustomCancellationStrategyTests
{
    public CustomCancellationStrategyJsonTypeHandlingTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new JsonMessageFormatter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            JsonSerializer =
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            },
        };

        this.clientMessageFormatter = new JsonMessageFormatter(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            JsonSerializer =
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            },
        };

        this.serverMessageHandler = new HeaderDelimitedMessageHandler(this.serverStream, this.serverStream, this.serverMessageFormatter);
        this.clientMessageHandler = new HeaderDelimitedMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
    }
}
