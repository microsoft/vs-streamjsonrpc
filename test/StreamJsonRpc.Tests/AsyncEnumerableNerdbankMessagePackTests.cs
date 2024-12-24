// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class AsyncEnumerableNerdbankMessagePackTests : AsyncEnumerableTests
{
    public AsyncEnumerableNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverFormatter = new();
        NerdbankMessagePackFormatter.FormatterProfile serverProfile = ConfigureContext(serverFormatter.ProfileBuilder);
        serverFormatter.SetFormatterProfile(serverProfile);

        NerdbankMessagePackFormatter clientFormatter = new();
        NerdbankMessagePackFormatter.FormatterProfile clientProfile = ConfigureContext(clientFormatter.ProfileBuilder);
        clientFormatter.SetFormatterProfile(clientProfile);

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;

        static NerdbankMessagePackFormatter.FormatterProfile ConfigureContext(NerdbankMessagePackFormatter.FormatterProfileBuilder profileBuilder)
        {
            profileBuilder.RegisterAsyncEnumerableType<IAsyncEnumerable<int>, int>();
            profileBuilder.AddTypeShapeProvider(PolyType.SourceGenerator.ShapeProvider_StreamJsonRpc_Tests.Default);
            profileBuilder.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
            return profileBuilder.Build();
        }
    }
}
