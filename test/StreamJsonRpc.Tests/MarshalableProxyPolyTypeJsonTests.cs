// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable PolyTypeJson

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc.Reflection;
using static JsonRpcTests;

public partial class MarshalableProxyPolyTypeJsonTests : MarshalableProxyTests
{
    public MarshalableProxyPolyTypeJsonTests(ITestOutputHelper logger)
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
                TypeInfoResolver = SourceGenerationContext5.Default,
            },
            TypeShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc_Tests.Default,
        };

        formatter.RegisterGenericType<int>();
        formatter.RegisterGenericType<IMarshalable>();
        formatter.RegisterGenericType<IGenericMarshalable<int>>();
        formatter.RegisterGenericType<IMarshalableAndSerializable>();
        formatter.RegisterGenericType<IMarshalableWithCallScopedLifetime>();
        formatter.RegisterGenericType<IMarshalableWithOptionalInterfaces>();
        formatter.RegisterGenericType<IMarshalableWithOptionalInterfaces2>();
        formatter.RegisterGenericType<IMarshalableSubType2>();

        return formatter;
    }

    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<int>))]
    [JsonSerializable(typeof(ExceptionWithAsyncEnumerable))]
    [JsonSerializable(typeof(JsonException))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(TypeThrowsWhenSerialized))]
    [JsonSerializable(typeof(IAsyncEnumerable<int>))]
    [JsonSerializable(typeof(IMarshalable))]
    [JsonSerializable(typeof(IMarshalableWithCallScopedLifetime))]
    [JsonSerializable(typeof(IMarshalableWithOptionalInterfaces))]
    [JsonSerializable(typeof(IMarshalableWithOptionalInterfaces2))]
    [JsonSerializable(typeof(IMarshalableSubType2))]
    [JsonSerializable(typeof(IMarshalableAndSerializable))]
    [JsonSerializable(typeof(DataContainer))]
    [JsonSerializable(typeof(ProxyContainer<IMarshalable>))]
    [JsonSerializable(typeof(ProxyContainer<IMarshalableAndSerializable>))]
    [JsonSerializable(typeof(ProxyContainer<IGenericMarshalable<int>>))]
    private partial class SourceGenerationContext5 : JsonSerializerContext;

    [GenerateShapeFor<ExceptionWithAsyncEnumerable>]
    private partial class Witness;
}
