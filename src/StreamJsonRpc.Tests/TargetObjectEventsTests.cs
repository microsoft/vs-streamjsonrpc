using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class TargetObjectEventsTests : TestBase
{
    private readonly Server server;
    private readonly Client client;

    private FullDuplexStream serverStream;
    private JsonRpc serverRpc;

    private FullDuplexStream clientStream;
    private JsonRpc clientRpc;

    public TargetObjectEventsTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();
        this.client = new Client();

        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = JsonRpc.Attach(this.serverStream, this.server);
        this.clientRpc = JsonRpc.Attach(this.clientStream, this.client);
    }

    [Fact]
    public async Task ServerEventRaisesCallback()
    {
        var tcs = new TaskCompletionSource<EventArgs>();
        this.client.ServerEventRaised = (sender, args) => tcs.SetResult(args);
        this.server.TriggerEvent(EventArgs.Empty);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.NotNull(actualArgs);
    }

    [Fact]
    public async Task GenericServerEventRaisesCallback()
    {
        var tcs = new TaskCompletionSource<CustomEventArgs>();
        var expectedArgs = new CustomEventArgs { Seeds = 5 };
        this.client.GenericServerEventRaised = (sender, args) => tcs.SetResult(args);
        this.server.TriggerGenericEvent(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedArgs.Seeds, actualArgs.Seeds);
    }

    /// <summary>
    /// Verifies that events on target objects are subscribed to, and unsubscribed after the JsonRpc instance is disposed of.
    /// </summary>
    [Fact]
    public void ServerEventSubscriptionLifetime()
    {
        Assert.NotNull(this.server.ServerEventAccessor);
        this.serverRpc.Dispose();
        Assert.Null(this.server.ServerEventAccessor);
    }

    /// <summary>
    /// Verifies that generic event handler events on target objects are subscribed to, and unsubscribed after the JsonRpc instance is disposed of.
    /// </summary>
    [Fact]
    public void GenericServerEventSubscriptionLifetime()
    {
        Assert.NotNull(this.server.ServerEventWithCustomArgsAccessor);
        this.serverRpc.Dispose();
        Assert.Null(this.server.ServerEventWithCustomArgsAccessor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.serverRpc.Dispose();
            this.clientRpc.Dispose();
        }

        base.Dispose(disposing);
    }

    private class Client
    {
        internal Action<object, EventArgs> ServerEventRaised { get; set; }

        internal Action<object, CustomEventArgs> GenericServerEventRaised { get; set; }

        public void ServerEvent(object sender, EventArgs args) => this.ServerEventRaised?.Invoke(sender, args);

        public void ServerEventWithCustomArgs(object sender, CustomEventArgs args) => this.GenericServerEventRaised?.Invoke(sender, args);
    }

    private class Server
    {
        public event EventHandler ServerEvent;

        public event EventHandler<CustomEventArgs> ServerEventWithCustomArgs;

        internal EventHandler ServerEventAccessor => this.ServerEvent;

        internal EventHandler<CustomEventArgs> ServerEventWithCustomArgsAccessor => this.ServerEventWithCustomArgs;

        public void TriggerEvent(EventArgs args)
        {
            this.OnServerEvent(args);
        }

        public void TriggerGenericEvent(CustomEventArgs args)
        {
            this.OnServerEventWithCustomArgs(args);
        }

        protected virtual void OnServerEvent(EventArgs args) => this.ServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomArgs(CustomEventArgs args) => this.ServerEventWithCustomArgs?.Invoke(this, args);
    }

    private class CustomEventArgs : EventArgs
    {
        public int Seeds { get; set; }
    }
}
