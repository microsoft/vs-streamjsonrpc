// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class ObserverMarshalingNerdbankMessagePackTests : ObserverMarshalingTests
{
    public ObserverMarshalingNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter();
}
