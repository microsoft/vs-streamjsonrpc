// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc;
using Xunit.Abstractions;

public class DuplexPipeMarshalingJsonTests : DuplexPipeMarshalingTests
{
    public DuplexPipeMarshalingJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new JsonMessageFormatter { MultiplexingStream = this.serverMx };
        this.clientMessageFormatter = new JsonMessageFormatter { MultiplexingStream = this.clientMx };
    }
}
