// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class DuplexPipeMarshalingSystemTextJsonTests : DuplexPipeMarshalingTests
{
    public DuplexPipeMarshalingSystemTextJsonTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new SystemTextJsonFormatter { MultiplexingStream = this.serverMx };
        this.clientMessageFormatter = new SystemTextJsonFormatter { MultiplexingStream = this.clientMx };
    }
}
