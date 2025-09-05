// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;

public partial class DisposableProxyNerdbankMessagePackTests(ITestOutputHelper logger) : DisposableProxyTests(logger)
{
    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.ShapeProvider };

    [GenerateShapeFor<bool>]
    private partial class Witness;
}
