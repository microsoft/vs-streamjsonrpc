// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc;

public class JsonRpcRemoteTargetJsonMessageFormatterTests : JsonRpcRemoteTargetTests
{
    public JsonRpcRemoteTargetJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override IJsonRpcMessageFormatter CreateFormatter() => new JsonMessageFormatter();
}
