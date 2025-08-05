// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using PolyType;

public partial class DuplexPipeMarshalingNerdbankMessagePackTests : DuplexPipeMarshalingTests
{
    public DuplexPipeMarshalingNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverFormatter = new()
        {
            MultiplexingStream = this.serverMx,
            TypeShapeProvider = Witness.ShapeProvider,
        };

        NerdbankMessagePackFormatter clientFormatter = new()
        {
            MultiplexingStream = this.clientMx,
            TypeShapeProvider = Witness.ShapeProvider,
        };

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;
    }

    [GenerateShapeFor<IDuplexPipe>]
    [GenerateShapeFor<Stream>]
    [GenerateShapeFor<PipeReader>]
    [GenerateShapeFor<PipeWriter>]
    private partial class Witness;
}
