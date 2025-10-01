// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json.Serialization;

public partial class AsyncEnumerableSystemTextJsonTests : AsyncEnumerableTests
{
    public AsyncEnumerableSystemTextJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions =
            {
                TypeInfoResolver = SourceGenerationContext4.Default,
            },
        };
        this.clientMessageFormatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions =
            {
                TypeInfoResolver = SourceGenerationContext4.Default,
            },
        };
    }

    [JsonSerializable(typeof(IAsyncEnumerable<int>))]
    private partial class SourceGenerationContext4 : JsonSerializerContext;
}
