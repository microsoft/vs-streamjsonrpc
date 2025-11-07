// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable PolyTypeJson

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc.Reflection;

public partial class AsyncEnumerablePolyTypeJsonTests : AsyncEnumerableTests
{
    public AsyncEnumerablePolyTypeJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        static PolyTypeJsonFormatter CreateFormatter()
        {
            var formatter = new PolyTypeJsonFormatter
            {
                JsonSerializerOptions =
            {
                TypeInfoResolver = SourceGenerationContext4.Default,
            },
                TypeShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc_Tests.Default,
            };

            formatter.RegisterGenericType<int>();
            formatter.RegisterGenericType<string>();

            return formatter;
        }

        this.serverMessageFormatter = CreateFormatter();
        this.clientMessageFormatter = CreateFormatter();
    }

    [JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<int>))]
    [JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<string>))]
    [JsonSerializable(typeof(CompoundEnumerableResult))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(IAsyncEnumerable<int>))]
    [JsonSerializable(typeof(IAsyncEnumerable<string>))]
    private partial class SourceGenerationContext4 : JsonSerializerContext;
}
