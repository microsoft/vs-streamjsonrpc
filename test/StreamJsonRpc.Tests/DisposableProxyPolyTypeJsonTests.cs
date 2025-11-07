// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable PolyTypeJson

using System.Text.Json;
using System.Text.Json.Serialization;
using static JsonRpcTests;

public partial class DisposableProxyPolyTypeJsonTests : DisposableProxyTests
{
    public DisposableProxyPolyTypeJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(JsonException);

    protected override IJsonRpcMessageFormatter CreateFormatter()
    {
        var formatter = new PolyTypeJsonFormatter
        {
            JsonSerializerOptions =
            {
                TypeInfoResolver = SourceGenerationContext3.Default,
            },
            TypeShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc_Tests.Default,
        };

        return formatter;
    }

    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(DataContainer))]
    [JsonSerializable(typeof(ProxyContainer))]
    [JsonSerializable(typeof(IDisposable))]
    [JsonSerializable(typeof(TypeThrowsWhenSerialized))]
    private partial class SourceGenerationContext3 : JsonSerializerContext;
}
