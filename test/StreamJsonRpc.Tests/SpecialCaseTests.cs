// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Nerdbank.Streams;

public class SpecialCaseTests : TestBase
{
    public SpecialCaseTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public async Task ExceptionDetailsSurviveMultipleRpcHops_CommonErrorData()
    {
        await this.ExceptionDetailsSurviveMultipleRpcHopsAsync(ExceptionProcessing.CommonErrorData);
    }

    [Fact]
    public async Task ExceptionDetailsSurviveMultipleRpcHops_ISerializable()
    {
        RemoteInvocationException exception = await this.ExceptionDetailsSurviveMultipleRpcHopsAsync(ExceptionProcessing.ISerializable);
        RemoteInvocationException forwardingException = Assert.IsType<RemoteInvocationException>(exception.InnerException);
        InvalidOperationException originalException = Assert.IsType<InvalidOperationException>(forwardingException.InnerException);
        Assert.Equal(ThrowingServer.ExceptionMessage, originalException.Message);
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

    [Fact]
    public async Task TraceListenerThrows_CausesDisconnect()
    {
        var pair = FullDuplexStream.CreatePair();
        var serverRpc = new JsonRpc(pair.Item1)
        {
            TraceSource =
            {
                Switch = { Level = SourceLevels.All },
                Listeners = { new ThrowingTraceListener() },
            },
        };
        serverRpc.StartListening();
        int bytesRead = await pair.Item2.ReadAsync(new byte[1], 0, 1, this.TimeoutToken);
        Assert.Equal(0, bytesRead);
    }

    private async Task<RemoteInvocationException> ExceptionDetailsSurviveMultipleRpcHopsAsync(ExceptionProcessing exceptionStrategy)
    {
        (Stream firstClientStream, Stream firstServerStream) = FullDuplexStream.CreatePair();
        (Stream secondClientStream, Stream secondServerStream) = FullDuplexStream.CreatePair();
        using JsonRpc secondClient = CreateJsonRpc(secondClientStream, exceptionStrategy);
        using JsonRpc secondServer = CreateJsonRpc(secondServerStream, exceptionStrategy, new ThrowingServer());
        using JsonRpc firstServer = CreateJsonRpc(firstServerStream, exceptionStrategy, new ForwardingServer(secondClient));
        using JsonRpc firstClient = CreateJsonRpc(firstClientStream, exceptionStrategy);

        RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => firstClient.InvokeAsync(nameof(ForwardingServer.ForwardAsync)));

        CommonErrorData forwardingError = Assert.IsType<CommonErrorData>(exception.DeserializedErrorData);
        Assert.Equal(typeof(RemoteInvocationException).FullName, forwardingError.TypeName);
        Assert.Contains(nameof(ForwardingServer.ForwardAsync), forwardingError.StackTrace);

        CommonErrorData originalError = Assert.IsType<CommonErrorData>(forwardingError.Inner);
        Assert.Equal(typeof(InvalidOperationException).FullName, originalError.TypeName);
        Assert.Equal(ThrowingServer.ExceptionMessage, originalError.Message);
        Assert.Contains(nameof(ThrowingServer.Throw), originalError.StackTrace);
        return exception;

        static JsonRpc CreateJsonRpc(Stream stream, ExceptionProcessing exceptionStrategy, object? target = null)
        {
            var rpc = new JsonRpc(stream)
            {
                ExceptionStrategy = exceptionStrategy,
            };
            if (target is not null)
            {
                rpc.AddLocalRpcTarget(target);
            }

            rpc.StartListening();
            return rpc;
        }
    }

    private class Server
    {
        public void Hi()
        {
        }
    }

    private class ForwardingServer
    {
        private readonly JsonRpc nextClient;

        internal ForwardingServer(JsonRpc nextClient)
        {
            this.nextClient = nextClient;
        }

        public async Task ForwardAsync() => await this.nextClient.InvokeAsync(nameof(ThrowingServer.Throw));
    }

    private class ThrowingServer
    {
        internal const string ExceptionMessage = "Exception from the second server.";

        public void Throw() => throw new InvalidOperationException(ExceptionMessage);
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

    private class ThrowingTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            throw new NotImplementedException();
        }

        public override void WriteLine(string? message)
        {
            throw new NotImplementedException();
        }
    }
}
