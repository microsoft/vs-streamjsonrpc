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
        this.serverMessageFormatter = new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.ShapeProvider };
        this.clientMessageFormatter = new NerdbankMessagePackFormatter() { TypeShapeProvider = Witness.ShapeProvider };
    }

    [GenerateShapeFor<IReadOnlyList<int>>]
    [GenerateShapeFor<IReadOnlyList<string>>]
    [GenerateShapeFor<IReadOnlyList<CompoundEnumerableResult>>]
    [GenerateShapeFor<IAsyncEnumerable<string>>]
    [GenerateShapeFor<CompoundEnumerableResult>]
    private partial class Witness;
}
