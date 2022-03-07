// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc;
using Xunit.Abstractions;

public class MarshalableProxyMessagePackTests : MarshalableProxyTests
{
    public MarshalableProxyMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override IJsonRpcMessageFormatter CreateFormatter() => new MessagePackFormatter();
}
