// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Xunit.Abstractions;

public class JsonRpcClient20InteropJsonTypeHandlingTests : JsonRpcClient20InteropTests
{
    public JsonRpcClient20InteropJsonTypeHandlingTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.UseTypeHandling();
    }
}
