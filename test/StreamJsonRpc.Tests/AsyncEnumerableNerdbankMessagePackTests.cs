// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

public partial class AsyncEnumerableNerdbankMessagePackTests : AsyncEnumerableTests
{
    public AsyncEnumerableNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.GeneratedTypeShapeProvider };
        this.clientMessageFormatter = new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.GeneratedTypeShapeProvider };
    }

    [GenerateShapeFor<bool>]
    private partial class Witness;
}
