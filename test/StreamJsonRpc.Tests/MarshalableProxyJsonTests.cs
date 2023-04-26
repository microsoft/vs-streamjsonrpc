// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Newtonsoft.Json;

public class MarshalableProxyJsonTests : MarshalableProxyTests
{
    public MarshalableProxyJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(JsonSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new JsonMessageFormatter();
}
