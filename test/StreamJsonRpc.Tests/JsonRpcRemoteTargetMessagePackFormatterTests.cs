// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

/* This test class enables a set of tests for a scenario that doesn't work on the MessagePackFormatter today.

using StreamJsonRpc;

public class JsonRpcRemoteTargetMessagePackFormatterTests : JsonRpcRemoteTargetTests
{
    public JsonRpcRemoteTargetMessagePackFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override IJsonRpcMessageFormatter CreateFormatter() => new MessagePackFormatter();

    protected override IJsonRpcMessageHandler CreateHandler(Stream sending, Stream receiving) => new LengthHeaderMessageHandler(sending, receiving, this.CreateFormatter());
}
*/
