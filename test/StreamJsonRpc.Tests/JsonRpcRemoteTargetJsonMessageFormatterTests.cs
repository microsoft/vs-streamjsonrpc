// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[Obsolete(ObsoleteFormatter)]
public class JsonRpcRemoteTargetJsonMessageFormatterTests : JsonRpcRemoteTargetTests
{
    public JsonRpcRemoteTargetJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }

    protected override IJsonRpcMessageHandler CreateHandler(Stream sending, Stream receiving) => new HeaderDelimitedMessageHandler(sending, receiving, new JsonMessageFormatter());
}
