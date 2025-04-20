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

    protected override IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.ShapeProvider };

    [GenerateShape<Data>]
    [GenerateShape<DataContainer>]
    [GenerateShape<IMarshalable>]
    [GenerateShape<ProxyContainer<IMarshalable>>]
    [GenerateShape<ProxyContainer<IMarshalableAndSerializable>>]
    [GenerateShape<ProxyContainer<IGenericMarshalable<int>>>]
    [GenerateShape<IMarshalableWithProperties>]
    [GenerateShape<IMarshalableWithEvents>]
    [GenerateShape<IMarshalableAndSerializable>]
    [GenerateShape<IMarshalableWithCallScopedLifetime>]
    [GenerateShape<INonDisposableMarshalable>]
    [GenerateShape<IMarshalableSubType1>]
    [GenerateShape<IMarshalableSubType2>]
    [GenerateShape<IMarshalableSubType1Extended>]
    [GenerateShape<IMarshalableNonExtendingBase>]
    [GenerateShape<IMarshalableSubTypesCombined>]
    [GenerateShape<IMarshalableSubTypeWithIntermediateInterface>]
    [GenerateShape<IMarshalableSubTypeWithIntermediateInterface2>]
    [GenerateShape<IMarshalableWithOptionalInterfaces>]
    [GenerateShape<IMarshalableWithOptionalInterfaces2>]
    [GenerateShape<IMarshalableSubType2Extended>]
    [GenerateShape<IGenericMarshalable<int>>]
    private partial class Witness;
}
