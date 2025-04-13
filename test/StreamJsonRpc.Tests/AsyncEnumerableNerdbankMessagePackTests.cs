﻿// Copyright (c) Microsoft Corporation. All rights reserved.
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

    [GenerateShape<IReadOnlyList<int>>]
    [GenerateShape<IReadOnlyList<string>>]
    [GenerateShape<IReadOnlyList<CompoundEnumerableResult>>]
    [GenerateShape<IAsyncEnumerable<string>>]
    [GenerateShape<CompoundEnumerableResult>]
    [GenerateShape<StreamJsonRpc.Reflection.MessageFormatterEnumerableTracker.EnumeratorResults<string>>] // https://github.com/eiriktsarpalis/PolyType/issues/146
    private partial class Witness;
}
