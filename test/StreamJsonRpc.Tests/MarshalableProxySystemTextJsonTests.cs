// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;

public class MarshalableProxySystemTextJsonTests : MarshalableProxyTests
{
    public MarshalableProxySystemTextJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(JsonException);

    protected override IJsonRpcMessageFormatter CreateFormatter() => new SystemTextJsonFormatter();
}
