// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc;
using Xunit.Abstractions;

public class DuplexPipeMarshalingMessagePackTests : DuplexPipeMarshalingTests
{
    public DuplexPipeMarshalingMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new MessagePackFormatter { MultiplexingStream = this.serverMx };
        this.clientMessageFormatter = new MessagePackFormatter { MultiplexingStream = this.clientMx };
    }
}
