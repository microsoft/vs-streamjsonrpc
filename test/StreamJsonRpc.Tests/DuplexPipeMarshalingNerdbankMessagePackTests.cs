// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.Streams;

public class DuplexPipeMarshalingNerdbankMessagePackTests : DuplexPipeMarshalingTests
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
        };

        serverFormatter.SetFormatterProfile(b =>
        {
            b.RegisterDuplexPipeType<MultiplexingStream.Channel>();
            b.AddTypeShapeProvider(PolyType.SourceGenerator.ShapeProvider_StreamJsonRpc_Tests.Default);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
            return b.Build();
        });

        NerdbankMessagePackFormatter clientFormatter = new()
        {
            MultiplexingStream = this.clientMx,
        };

        clientFormatter.SetFormatterProfile(b =>
        {
            b.RegisterDuplexPipeType<MultiplexingStream.Channel>();
            b.AddTypeShapeProvider(PolyType.SourceGenerator.ShapeProvider_StreamJsonRpc_Tests.Default);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
            return b.Build();
        });

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;
    }
}
