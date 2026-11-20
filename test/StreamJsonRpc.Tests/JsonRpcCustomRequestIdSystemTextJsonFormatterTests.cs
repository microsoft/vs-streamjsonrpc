// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class JsonRpcCustomRequestIdSystemTextJsonFormatterTests : JsonRpcCustomRequestIdTests
{
    public JsonRpcCustomRequestIdSystemTextJsonFormatterTests(ITestOutputHelper logger)
        : base(new SystemTextJsonFormatter(), logger)
    {
    }
}
