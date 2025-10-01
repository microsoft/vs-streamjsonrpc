// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

public partial class MarshalableProxySystemTextJsonTests : MarshalableProxyTests
{
    public MarshalableProxySystemTextJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(JsonException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter
    {
        JsonSerializerOptions =
            {
                TypeInfoResolver = SourceGenerationContext5.Default,
            },
    };

    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(IMarshalable))]
    [JsonSerializable(typeof(IMarshalableWithOptionalInterfaces))]
    [JsonSerializable(typeof(DataContainer))]
    [JsonSerializable(typeof(ProxyContainer<IGenericMarshalable<int>>))]
    private partial class SourceGenerationContext5 : JsonSerializerContext;
}
