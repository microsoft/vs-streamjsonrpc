// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

public class ObserverMarshalingNerdbankMessagePackTests : ObserverMarshalingTests
{
    public ObserverMarshalingNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override IJsonRpcMessageFormatter CreateFormatter()
    {
        NerdbankMessagePackFormatter formatter = new();
        formatter.SetFormatterProfile(b =>
        {
            b.RegisterRpcMarshalableConverter<IObserver<int>>();
            b.RegisterExceptionType<ApplicationException>();
            b.AddTypeShapeProvider(ObserverMarshalingWitness.ShapeProvider);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        return formatter;
    }
}

[GenerateShape<ApplicationException>]
#pragma warning disable SA1402 // File may only contain a single type
internal partial class ObserverMarshalingWitness;
#pragma warning restore SA1402 // File may only contain a single type
