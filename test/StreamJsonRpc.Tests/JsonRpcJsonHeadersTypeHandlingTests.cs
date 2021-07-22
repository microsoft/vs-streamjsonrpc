// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
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

    /// <summary>
    /// Method overriden because anonymous types are not supported when TypeHandling is set to "Object" or "Auto".
    /// </summary>
    [Fact]
    public override async Task CanPassAndCallPrivateMethodsObjects()
    {
        var result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new Foo { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);
    }

    [Fact]
    public override Task InvokeWithArrayParameters_SendingWithNullProgressConcreteTypeProperty()
    {
        // This method is not tested because TypeHandling will prevent this from working. Types have to match.
        return Task.CompletedTask;
    }

    [Fact]
    public override Task InvokeWithArrayParameters_SendingWithProgressConcreteTypeProperty()
    {
        // This method is not tested because TypeHandling will prevent this from working. Types have to match.
        return Task.CompletedTask;
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
