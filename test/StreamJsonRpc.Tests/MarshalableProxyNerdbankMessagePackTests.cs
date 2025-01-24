// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;

public partial class MarshalableProxyNerdbankMessagePackTests : MarshalableProxyTests
{
    public MarshalableProxyNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter()
    {
        NerdbankMessagePackFormatter formatter = new();
        formatter.SetFormatterProfile(b =>
        {
            b.RegisterRpcMarshalableConverter<IMarshalableAndSerializable>();
            b.RegisterRpcMarshalableConverter<IMarshalable>();
            b.RegisterRpcMarshalableConverter<IMarshalableWithCallScopedLifetime>();
            b.RegisterRpcMarshalableConverter<INonDisposableMarshalable>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubType1>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubType2>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubType1Extended>();
            b.RegisterRpcMarshalableConverter<IMarshalableNonExtendingBase>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubTypesCombined>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubTypeWithIntermediateInterface>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubTypeWithIntermediateInterface2>();
            b.RegisterRpcMarshalableConverter<IMarshalableWithOptionalInterfaces2>();
            b.RegisterRpcMarshalableConverter<IMarshalableSubType2Extended>();
            b.RegisterRpcMarshalableConverter<IGenericMarshalable<int>>();
            b.AddTypeShapeProvider(MarshalableProxyWitness.ShapeProvider);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        return formatter;
    }

    [GenerateShape<Data>]
    [GenerateShape<IMarshalableWithProperties>]
    [GenerateShape<IMarshalableWithEvents>]
    public partial class MarshalableProxyWitness;
}
