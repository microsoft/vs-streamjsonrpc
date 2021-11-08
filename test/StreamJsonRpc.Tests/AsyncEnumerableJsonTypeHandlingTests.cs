// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit.Abstractions;

public class AsyncEnumerableJsonTypeHandlingTests : AsyncEnumerableJsonTests
{
    public AsyncEnumerableJsonTypeHandlingTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                TypeNameHandling = TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            },
        };

        this.clientMessageFormatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                TypeNameHandling = TypeNameHandling.Objects,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
            },
        };
    }
}
