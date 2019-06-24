using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Information);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Information);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServerEventRespondsToOptions(bool registerOn)
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1, streams.Item1);
        var options = new JsonRpcTargetOptions { NotifyClientOfEvents = registerOn };
        var server = new Server();
        rpc.AddLocalRpcTarget(server, options);
        if (registerOn)
        {
            Assert.NotNull(server.ServerEventAccessor);
            Assert.NotNull(server.ServerEventWithCustomArgsAccessor);
        }
        else
        {
            Assert.Null(server.ServerEventAccessor);
            Assert.Null(server.ServerEventWithCustomArgsAccessor);
        }
    }

    [Fact]
    public async Task ServerEventRaisesCallback()
    {
        var tcs = new TaskCompletionSource<EventArgs>();
        this.client.ServerEventRaised = args => tcs.SetResult(args);
        this.server.TriggerEvent(EventArgs.Empty);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.NotNull(actualArgs);
    }

    [Fact]
    public async Task StaticServerEventDoesNotRaiseCallback()
    {
        var tcs = new TaskCompletionSource<EventArgs>();
        this.client.PublicStaticServerEventRaised = args => tcs.SetResult(args);
        Server.TriggerPublicStaticServerEvent(EventArgs.Empty);
        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WithTimeout(ExpectedTimeout));
    }

    [Fact]
    public async Task GenericServerEventRaisesCallback()
    {
        var tcs = new TaskCompletionSource<CustomEventArgs>();
        var expectedArgs = new CustomEventArgs { Seeds = 5 };
        this.client.GenericServerEventRaised = args => tcs.SetResult(args);
        this.server.TriggerGenericEvent(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedArgs.Seeds, actualArgs.Seeds);
    }

    [Fact]
    public async Task CustomDelegateServerEventRaisesCallback()
    {
        var tcs = new TaskCompletionSource<MessageEventArgs<string>>();
        var expectedArgs = new MessageEventArgs<string> { Message = "a" };
        this.client.ServerEventWithCustomGenericDelegateAndArgsRaised = args => tcs.SetResult(args);
        this.server.TriggerServerEventWithCustomGenericDelegateAndArgs(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal<string>(expectedArgs.Message, actualArgs.Message);
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

    [Fact]
    public void IncompatibleEventHandlerType()
    {
        var streams = FullDuplexStream.CreateStreams();
        Assert.Throws<NotSupportedException>(() => JsonRpc.Attach(streams.Item1, new ServerWithIncompatibleEvents()));
    }

    [Fact]
    public void EventsAreNotOfferedAsTargetMethods()
    {
        Func<string, string> methodNameTransform = clrMethodName =>
        {
            Assert.NotEqual($"add_{nameof(Server.ServerEvent)}", clrMethodName);
            Assert.DoesNotContain($"get_{nameof(Server.ServerEventAccessor)}", clrMethodName);
            return clrMethodName;
        };

        var serverRpc = new JsonRpc(this.serverStream, this.serverStream);
        serverRpc.AddLocalRpcTarget(this.server, new JsonRpcTargetOptions { MethodNameTransform = methodNameTransform });
    }

    [Fact]
    public async Task NameTransformIsUsedWhenRaisingEvent()
    {
        bool eventNameTransformSeen = false;
        Func<string, string> eventNameTransform = name =>
        {
            if (name == nameof(Server.ServerEvent))
            {
                eventNameTransformSeen = true;
            }

            return "ns." + name;
        };

        // Create new streams since we are going to use our own client/server
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;
        var serverWithTransform = new Server();
        var clientWithoutTransform = new Client();
        var clientWithTransform = new Client();

        this.serverRpc = new JsonRpc(this.serverStream, this.serverStream);
        this.clientRpc = new JsonRpc(this.clientStream, this.clientStream);

        this.serverRpc.AddLocalRpcTarget(serverWithTransform, new JsonRpcTargetOptions { EventNameTransform = eventNameTransform });

        // We have to use MethodNameTransform here as Client uses methods to listen for events on server
        this.clientRpc.AddLocalRpcTarget(clientWithoutTransform);
        this.clientRpc.AddLocalRpcTarget(clientWithTransform, new JsonRpcTargetOptions { MethodNameTransform = eventNameTransform });
        this.clientRpc.StartListening();

        var tcsWithoutTransform = new TaskCompletionSource<EventArgs>();
        clientWithoutTransform.ServerEventRaised = args => tcsWithoutTransform.SetResult(args);

        var tcsWithTransform = new TaskCompletionSource<EventArgs>();
        clientWithTransform.ServerEventRaised = args => tcsWithTransform.SetResult(args);

        serverWithTransform.TriggerEvent(EventArgs.Empty);

        var actualArgs = await tcsWithTransform.Task.WithCancellation(this.TimeoutToken);
        Assert.NotNull(actualArgs);
        await Assert.ThrowsAsync<TimeoutException>(() => tcsWithoutTransform.Task.WithTimeout(ExpectedTimeout));
        Assert.True(eventNameTransformSeen);
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
        internal Action<EventArgs> ServerEventRaised { get; set; }

        internal Action<EventArgs> PublicStaticServerEventRaised { get; set; }

        internal Action<CustomEventArgs> GenericServerEventRaised { get; set; }

        internal Action<MessageEventArgs<string>> ServerEventWithCustomGenericDelegateAndArgsRaised { get; set; }

        public void ServerEvent(EventArgs args) => this.ServerEventRaised?.Invoke(args);

        public void PublicStaticServerEvent(EventArgs args) => this.PublicStaticServerEventRaised?.Invoke(args);

        public void ServerEventWithCustomArgs(CustomEventArgs args) => this.GenericServerEventRaised?.Invoke(args);

        public void ServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.ServerEventWithCustomGenericDelegateAndArgsRaised?.Invoke(args);
    }

    private class Server
    {
        public delegate void MessageReceivedEventHandler<T>(object sender, MessageEventArgs<T> args);

        public static event EventHandler PublicStaticServerEvent;

        public event EventHandler ServerEvent;

        public event EventHandler<CustomEventArgs> ServerEventWithCustomArgs;

        public event MessageReceivedEventHandler<string> ServerEventWithCustomGenericDelegateAndArgs;

        private static event EventHandler PrivateStaticServerEvent;

        private event EventHandler PrivateServerEvent;

        internal EventHandler ServerEventAccessor => this.ServerEvent;

        internal EventHandler<CustomEventArgs> ServerEventWithCustomArgsAccessor => this.ServerEventWithCustomArgs;

        public static void TriggerPublicStaticServerEvent(EventArgs args) => PublicStaticServerEvent?.Invoke(null, args);

        public void TriggerEvent(EventArgs args)
        {
            this.OnServerEvent(args);
        }

        public void TriggerGenericEvent(CustomEventArgs args)
        {
            this.OnServerEventWithCustomArgs(args);
        }

        public void TriggerServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.OnServerEventWithCustomGenericDelegateAndArgs(args);

        protected static void OnPrivateStaticServerEvent(EventArgs args) => PrivateStaticServerEvent?.Invoke(null, args);

        protected virtual void OnServerEvent(EventArgs args) => this.ServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomArgs(CustomEventArgs args) => this.ServerEventWithCustomArgs?.Invoke(this, args);

        protected virtual void OnPrivateServerEvent(EventArgs args) => this.PrivateServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.ServerEventWithCustomGenericDelegateAndArgs?.Invoke(this, args);
    }

    private class ServerWithIncompatibleEvents
    {
        public delegate int MyDelegate(double d);

#pragma warning disable CS0067 // Unused member (It's here for reflection to discover)
        public event MyDelegate MyEvent;
#pragma warning restore CS0067
    }

    private class CustomEventArgs : EventArgs
    {
        public int Seeds { get; set; }
    }

    private class MessageEventArgs<T> : EventArgs
    {
        public T Message { get; set; }
    }
}
