// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

public class DisposableProxyNerdbankMessagePackTests : DisposableProxyTests
{
    public DisposableProxyNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new NerdbankMessagePackFormatter();
}
