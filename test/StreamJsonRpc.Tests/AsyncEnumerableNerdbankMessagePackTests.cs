// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;
using static AsyncEnumerableTests;

public class AsyncEnumerableNerdbankMessagePackTests : AsyncEnumerableTests
{
    public AsyncEnumerableNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverFormatter = new();
        serverFormatter.SetFormatterProfile(ConfigureContext);

        NerdbankMessagePackFormatter clientFormatter = new();
        clientFormatter.SetFormatterProfile(ConfigureContext);

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;

        static void ConfigureContext(NerdbankMessagePackFormatter.Profile.Builder profileBuilder)
        {
            profileBuilder.RegisterAsyncEnumerableConverter<int>();
            profileBuilder.RegisterAsyncEnumerableConverter<string>();
            profileBuilder.AddTypeShapeProvider(AsyncEnumerableWitness.ShapeProvider);
            profileBuilder.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        }
    }
}

[GenerateShape<IReadOnlyList<int>>]
[GenerateShape<IReadOnlyList<string>>]
[GenerateShape<IReadOnlyList<CompoundEnumerableResult>>]
[GenerateShape<CompoundEnumerableResult>]
#pragma warning disable SA1402 // File may only contain a single type
public partial class AsyncEnumerableWitness;
#pragma warning restore SA1402 // File may only contain a single type
