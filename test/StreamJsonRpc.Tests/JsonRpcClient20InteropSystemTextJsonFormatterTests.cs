// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class JsonRpcClient20InteropSystemTextJsonFormatterTests : JsonRpcClient20InteropTests
{
    public JsonRpcClient20InteropSystemTextJsonFormatterTests(ITestOutputHelper logger)
        : base(new SystemTextJsonFormatter(), logger)
    {
    }
}
