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
        var evt = new AsyncManualResetEvent();
        this.client.ServerEventRaised = (sender, args) => evt.Set();
        await this.clientRpc.InvokeAsync(nameof(Server.TriggerEvent));
        await evt.WaitAsync().WithCancellation(this.TimeoutToken);
    }

    private class Client
    {
        internal Action<object, EventArgs> ServerEventRaised { get; set; }

        public void ServerEvent(object sender, EventArgs args) => this.ServerEventRaised?.Invoke(sender, args);
    }

    private class Server
    {
        public event EventHandler ServerEvent;

        public void TriggerEvent(EventArgs args)
        {
            this.OnServerEvent(args);
        }

        protected virtual void OnServerEvent(EventArgs args) => this.ServerEvent?.Invoke(this, args);
    }
}
