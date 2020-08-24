// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc;
using Xunit.Abstractions;

public class DisposableProxyJsonTests : DisposableProxyTests
{
    public DisposableProxyJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override IJsonRpcMessageFormatter CreateFormatter() => new JsonMessageFormatter();
}
