// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class SpecialCaseTests : TestBase
{
    public SpecialCaseTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    /// <summary>
    /// Verifies that if the server fails to transmit a response, it drops the connection to avoid a client hang
    /// while waiting for the response.
    /// </summary>
    [Fact]
    public async Task ResponseTransmissionFailureDropsConnection()
    {
        var pair = FullDuplexStream.CreatePair();
        var clientRpc = JsonRpc.Attach(pair.Item1);
        var serverRpc = new JsonRpc(new ThrowingMessageHandler(pair.Item2), new Server());
        serverRpc.StartListening();
        await Assert.ThrowsAsync<ConnectionLostException>(() => clientRpc.InvokeAsync("Hi"));
    }

    private class Server
    {
        public void Hi()
        {
        }
    }

    private class ThrowingMessageHandler : HeaderDelimitedMessageHandler
    {
        public ThrowingMessageHandler(Stream duplexStream)
            : base(duplexStream)
        {
        }

        protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            throw new FileNotFoundException();
        }
    }
}
