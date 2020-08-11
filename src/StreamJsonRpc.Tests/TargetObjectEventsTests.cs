using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
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
        this.server = new ServerDerived();
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

    public interface IServer
    {
        event EventHandler InterfaceEvent;

        event EventHandler ExplicitInterfaceImplementation_Event;
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
        Assert.Equal(expectedArgs.Message, actualArgs.Message);
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

    [Fact]
    public async Task AddLocalRpcTarget_OfT_Interface()
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = new JsonRpc(this.serverStream);
        this.serverRpc.AddLocalRpcTarget<IServer>(this.server, null);
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(this.clientStream);
        var eventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServer.InterfaceEvent), new Action<EventArgs>(eventRaised.SetResult));
        var explicitEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServer.ExplicitInterfaceImplementation_Event), new Action<EventArgs>(explicitEventRaised.SetResult));
        var classOnlyEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(Server.ServerEvent), new Action<EventArgs>(classOnlyEventRaised.SetResult));
        this.clientRpc.StartListening();

        // Verify that ordinary interface events can be raised.
        this.server.TriggerInterfaceEvent(new EventArgs());
        await eventRaised.Task.WithCancellation(this.TimeoutToken);

        // Verify that explicit interface implementation events can also be raised.
        this.server.TriggerExplicitInterfaceImplementationEvent(new EventArgs());
        await explicitEventRaised.Task.WithCancellation(this.TimeoutToken);

        // Verify that events that are NOT on the interface cannot be raised.
        this.server.TriggerEvent(new EventArgs());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classOnlyEventRaised.Task.WithCancellation(ExpectedTimeoutToken));
    }

    [Fact]
    public async Task AddLocalRpcTarget_OfT_ActualClass()
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = new JsonRpc(this.serverStream);
        this.serverRpc.AddLocalRpcTarget<Server>(this.server, null);
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(this.clientStream);
        var eventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServer.InterfaceEvent), new Action<EventArgs>(eventRaised.SetResult));
        var explicitEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServer.ExplicitInterfaceImplementation_Event), new Action<EventArgs>(explicitEventRaised.SetResult));
        var classOnlyEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(Server.ServerEvent), new Action<EventArgs>(classOnlyEventRaised.SetResult));
        this.clientRpc.StartListening();

        // Verify that ordinary interface events can be raised.
        this.server.TriggerInterfaceEvent(new EventArgs());
        await eventRaised.Task.WithCancellation(this.TimeoutToken);

        // Verify that explicit interface implementation events can also be raised.
        this.server.TriggerExplicitInterfaceImplementationEvent(new EventArgs());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => explicitEventRaised.Task.WithCancellation(ExpectedTimeoutToken));

        // Verify that events that are NOT on the interface can be raised.
        this.server.TriggerEvent(new EventArgs());
        await classOnlyEventRaised.Task.WithCancellation(this.TimeoutToken);
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
        internal Action<EventArgs>? ServerEventRaised { get; set; }

        internal Action<EventArgs>? PublicStaticServerEventRaised { get; set; }

        internal Action<CustomEventArgs>? GenericServerEventRaised { get; set; }

        internal Action<MessageEventArgs<string>>? ServerEventWithCustomGenericDelegateAndArgsRaised { get; set; }

        public void ServerEvent(EventArgs args) => this.ServerEventRaised?.Invoke(args);

        public void PublicStaticServerEvent(EventArgs args) => this.PublicStaticServerEventRaised?.Invoke(args);

        public void ServerEventWithCustomArgs(CustomEventArgs args) => this.GenericServerEventRaised?.Invoke(args);

        public void ServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.ServerEventWithCustomGenericDelegateAndArgsRaised?.Invoke(args);
    }

    private class Server : IServer
    {
        private EventHandler? explicitInterfaceImplementationEvent;

        public delegate void MessageReceivedEventHandler<T>(object sender, MessageEventArgs<T> args)
            where T : class;

        public static event EventHandler? PublicStaticServerEvent;

        public event EventHandler? ServerEvent;

        public event EventHandler<CustomEventArgs>? ServerEventWithCustomArgs;

        public event MessageReceivedEventHandler<string>? ServerEventWithCustomGenericDelegateAndArgs;

        public event EventHandler? InterfaceEvent;

        private static event EventHandler? PrivateStaticServerEvent;

        event EventHandler IServer.ExplicitInterfaceImplementation_Event
        {
            add => this.explicitInterfaceImplementationEvent += value;
            remove => this.explicitInterfaceImplementationEvent -= value;
        }

        private event EventHandler? PrivateServerEvent;

        internal EventHandler? ServerEventAccessor => this.ServerEvent;

        internal EventHandler<CustomEventArgs>? ServerEventWithCustomArgsAccessor => this.ServerEventWithCustomArgs;

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

        public void TriggerInterfaceEvent(EventArgs args) => this.InterfaceEvent?.Invoke(this, args);

        public void TriggerExplicitInterfaceImplementationEvent(EventArgs args) => this.explicitInterfaceImplementationEvent?.Invoke(this, args);

        protected static void OnPrivateStaticServerEvent(EventArgs args) => PrivateStaticServerEvent?.Invoke(null, args);

        protected virtual void OnServerEvent(EventArgs args) => this.ServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomArgs(CustomEventArgs args) => this.ServerEventWithCustomArgs?.Invoke(this, args);

        protected virtual void OnPrivateServerEvent(EventArgs args) => this.PrivateServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.ServerEventWithCustomGenericDelegateAndArgs?.Invoke(this, args);
    }

    private class ServerDerived : Server
    {
    }

    private class ServerWithIncompatibleEvents
    {
        public delegate int MyDelegate(double d);

#pragma warning disable CS0067 // Unused member (It's here for reflection to discover)
        public event MyDelegate? MyEvent;
#pragma warning restore CS0067
    }

    private class CustomEventArgs : EventArgs
    {
        public int Seeds { get; set; }
    }

    private class MessageEventArgs<T> : EventArgs
        where T : class
    {
        public T? Message { get; set; }
    }
}
