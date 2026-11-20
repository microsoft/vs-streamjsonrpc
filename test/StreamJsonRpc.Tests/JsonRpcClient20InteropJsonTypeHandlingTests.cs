// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Newtonsoft.Json;

[Obsolete(ObsoleteFormatter)]
public class JsonRpcClient20InteropJsonTypeHandlingTests : JsonRpcClient20InteropJsonMessageFormatterTests
{
    public JsonRpcClient20InteropJsonTypeHandlingTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.UseTypeHandling();
    }

    protected void UseTypeHandling()
    {
        var formatter = (JsonMessageFormatter)this.Formatter;
        formatter.JsonSerializer.TypeNameHandling = TypeNameHandling.Objects;
        formatter.JsonSerializer.TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple;
    }
}
