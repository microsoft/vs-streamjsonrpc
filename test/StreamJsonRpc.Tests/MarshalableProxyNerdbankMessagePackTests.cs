// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NBMSGPACK_MARSHALING_SUPPORT
public partial class MarshalableProxyNerdbankMessagePackTests : MarshalableProxyTests
{
    public MarshalableProxyNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.ShapeProvider };

    [GenerateShapeFor<Data>]
    [GenerateShapeFor<DataContainer>]
    [GenerateShapeFor<IMarshalable>]
    [GenerateShapeFor<ProxyContainer<IMarshalable>>]
    [GenerateShapeFor<ProxyContainer<IMarshalableAndSerializable>>]
    [GenerateShapeFor<ProxyContainer<IGenericMarshalable<int>>>]
    [GenerateShapeFor<IMarshalableWithProperties>]
    [GenerateShapeFor<IMarshalableWithEvents>]
    [GenerateShapeFor<IMarshalableAndSerializable>]
    [GenerateShapeFor<IMarshalableWithCallScopedLifetime>]
    [GenerateShapeFor<INonDisposableMarshalable>]
    [GenerateShapeFor<IMarshalableSubType1>]
    [GenerateShapeFor<IMarshalableSubType2>]
    [GenerateShapeFor<IMarshalableSubType1Extended>]
    [GenerateShapeFor<IMarshalableNonExtendingBase>]
    [GenerateShapeFor<IMarshalableSubTypesCombined>]
    [GenerateShapeFor<IMarshalableSubTypeWithIntermediateInterface>]
    [GenerateShapeFor<IMarshalableSubTypeWithIntermediateInterface2>]
    [GenerateShapeFor<IMarshalableWithOptionalInterfaces>]
    [GenerateShapeFor<IMarshalableWithOptionalInterfaces2>]
    [GenerateShapeFor<IMarshalableSubType2Extended>]
    [GenerateShapeFor<IGenericMarshalable<int>>]
    [GenerateShapeFor<IAsyncEnumerable<int>>]
    [GenerateShapeFor<IAsyncEnumerable<string>>]
    private partial class Witness;
}
#endif
