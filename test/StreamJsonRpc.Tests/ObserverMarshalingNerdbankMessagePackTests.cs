// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NBMSGPACK_MARSHALING_SUPPORT
public partial class ObserverMarshalingNerdbankMessagePackTests(ITestOutputHelper logger) : ObserverMarshalingTests(logger)
{
    protected override IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter { TypeShapeProvider = Witness.ShapeProvider };

    [GenerateShapeFor<ApplicationException>]
    [GenerateShapeFor<IObserver<int>>]
    private partial class Witness;
}
#endif
