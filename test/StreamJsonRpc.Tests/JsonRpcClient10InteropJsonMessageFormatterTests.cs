// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[Obsolete(ObsoleteFormatter)]
public class JsonRpcClient10InteropJsonMessageFormatterTests : JsonRpcClient10InteropTests
{
    public JsonRpcClient10InteropJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }
}
