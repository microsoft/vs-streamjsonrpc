// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

public partial class DisposableProxySystemTextJsonTests : DisposableProxyTests
{
    public DisposableProxySystemTextJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(JsonException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter
    {
        JsonSerializerOptions =
        {
            TypeInfoResolver = SourceGenerationContext3.Default,
        },
    };

    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(DataContainer))]
    [JsonSerializable(typeof(IDisposable))]
    private partial class SourceGenerationContext3 : JsonSerializerContext;
}
