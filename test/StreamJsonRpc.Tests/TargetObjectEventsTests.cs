using System.Diagnostics;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using PolyType;

public abstract partial class TargetObjectEventsTests : TestBase
{
    protected IJsonRpcMessageHandler serverMessageHandler = null!;
    protected IJsonRpcMessageHandler clientMessageHandler = null!;
    protected FullDuplexStream serverStream = null!;
    protected FullDuplexStream clientStream = null!;

    private readonly Server server;
    private readonly Client client;

    private JsonRpc serverRpc;
    private JsonRpc clientRpc;

    public TargetObjectEventsTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new ServerDerived();
        this.client = new Client();

        this.ReinitializeRpcWithoutListening();
        Assumes.NotNull(this.serverRpc);
        Assumes.NotNull(this.clientRpc);

        this.serverRpc.StartListening();
        this.clientRpc.StartListening();
    }

    [MessagePack.Union(key: 0, typeof(Fruit))]
    [GenerateShape]
    [DerivedTypeShape(typeof(Fruit), Tag = 0)]
    public partial interface IFruit
    {
        string Name { get; }
    }

    [JsonRpcContract]
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IServer
    {
        event EventHandler InterfaceEvent;

        event EventHandler ExplicitInterfaceImplementation_Event;
    }

    [JsonRpcContract]
    [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IServerDerived : IServer
    {
        event EventHandler DerivedInterfaceEvent;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServerEventRespondsToOptions(bool registerOn)
    {
        var streams = FullDuplexStream.CreateStreams();
        var options = new JsonRpcTargetOptions { NotifyClientOfEvents = registerOn };
        var server = new Server();
        var rpc = this.CreateJsonRpcWithTargetObject(streams.Item1, server, options);
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
    public async Task EventWithInterfaceTypedArgument()
    {
        var tcs = new TaskCompletionSource<IFruit>();
        var expectedArgs = new Fruit("hi");
        this.client.ServerIFruitEventRaised = args => tcs.SetResult(args);
        this.server.TriggerIFruitEvent(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedArgs.Name, actualArgs.Name);
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

    /// <summary>
    /// Verifies that event handlers are unregistered when multiple connections are established and disposed.
    /// This tests for a memory leak where event handlers would accumulate if not properly unregistered on disposal.
    /// </summary>
    [Fact]
    public void EventHandlersUnregisteredOnMultipleConnectionDisposals()
    {
        // Use a shared server object across multiple connections to simulate the real-world scenario
        var sharedServer = new Server();

        // Verify no handlers initially
        Assert.Null(sharedServer.ServerEventAccessor);
        Assert.Null(sharedServer.ServerEventWithCustomArgsAccessor);

        // Create and dispose multiple connections
        for (int i = 0; i < 3; i++)
        {
            var streams = FullDuplexStream.CreateStreams();
            var rpc = this.CreateJsonRpcWithTargetObject(streams.Item1, sharedServer);
            rpc.StartListening();

            // Verify handler is registered
            Assert.NotNull(sharedServer.ServerEventAccessor);
            Assert.NotNull(sharedServer.ServerEventWithCustomArgsAccessor);

            // Count the number of handlers attached
            int serverEventHandlerCount = sharedServer.ServerEventAccessor?.GetInvocationList().Length ?? 0;
            int customArgsEventHandlerCount = sharedServer.ServerEventWithCustomArgsAccessor?.GetInvocationList().Length ?? 0;

            // Should only have one handler per event, not accumulating
            Assert.Equal(1, serverEventHandlerCount);
            Assert.Equal(1, customArgsEventHandlerCount);

            // Dispose the connection
            rpc.Dispose();

            // Verify handlers are unregistered after disposal
            Assert.Null(sharedServer.ServerEventAccessor);
            Assert.Null(sharedServer.ServerEventWithCustomArgsAccessor);
        }
    }

    /// <summary>
    /// Verifies that event handlers are unregistered when the stream is closed without explicit disposal.
    /// This simulates the scenario where a websocket connection drops unexpectedly.
    /// </summary>
    [Fact]
    public async Task EventHandlersUnregisteredWhenStreamClosesUnexpectedly()
    {
        var sharedServer = new Server();

        // Verify no handlers initially
        Assert.Null(sharedServer.ServerEventAccessor);

        var streams = FullDuplexStream.CreateStreams();
        var serverRpc = this.CreateJsonRpcWithTargetObject(streams.Item1, sharedServer);
        var clientRpc = new JsonRpc(streams.Item2);

        serverRpc.StartListening();
        clientRpc.StartListening();

        // Verify handler is registered
        Assert.NotNull(sharedServer.ServerEventAccessor);

        // Simulate connection drop by closing the stream without disposing JsonRpc
        streams.Item2.Dispose();

        // Wait for the disconnection to be detected
        await serverRpc.Completion.WithCancellation(this.TimeoutToken);

        // Verify handlers are unregistered after stream closure
        Assert.Null(sharedServer.ServerEventAccessor);
    }

    /// <summary>Ensures that JsonRpc only adds one event handler to target objects where events are declared multiple times in an type hierarchy.</summary>
    /// <remarks>This is a regression test for <see href="https://github.com/microsoft/vs-streamjsonrpc/issues/481">this bug</see>.</remarks>
    [Fact]
    public void EventOverridesStillGetJustOneHandler()
    {
        Assert.Equal(1, this.server.HandlersAttachedToAbstractBaseEvent);
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

        this.CreateJsonRpcWithTargetObject(this.serverStream, this.server, new JsonRpcTargetOptions { MethodNameTransform = methodNameTransform });
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

        this.serverRpc = this.CreateJsonRpcWithTargetObject(this.serverStream, serverWithTransform, new JsonRpcTargetOptions { EventNameTransform = eventNameTransform });
        this.clientRpc = new JsonRpc(this.clientStream, this.clientStream);

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

        this.serverRpc = this.CreateJsonRpcWithTargetObject<IServer>(this.serverStream, this.server);
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
    public async Task AddLocalRpcTarget_OfT_DerivedInterface()
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.serverRpc = this.CreateJsonRpcWithTargetObject<IServerDerived>(this.serverStream, this.server);
        this.serverRpc.StartListening();

        this.clientRpc = new JsonRpc(this.clientStream);
        var eventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServerDerived.InterfaceEvent), new Action<EventArgs>(eventRaised.SetResult));
        var derivedEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServerDerived.DerivedInterfaceEvent), new Action<EventArgs>(derivedEventRaised.SetResult));
        var explicitEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(IServerDerived.ExplicitInterfaceImplementation_Event), new Action<EventArgs>(explicitEventRaised.SetResult));
        var classOnlyEventRaised = new TaskCompletionSource<EventArgs>();
        this.clientRpc.AddLocalRpcMethod(nameof(Server.ServerEvent), new Action<EventArgs>(classOnlyEventRaised.SetResult));
        this.clientRpc.StartListening();

        // Verify that ordinary interface events can be raised.
        this.server.TriggerInterfaceEvent(new EventArgs());
        await eventRaised.Task.WithCancellation(this.TimeoutToken);

        // Verify that an event in the derived class still works.
        this.server.TriggerDerivedInterfaceEvent(new EventArgs());
        await derivedEventRaised.Task.WithCancellation(this.TimeoutToken);

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

        this.serverRpc = this.CreateJsonRpcWithTargetObject(this.serverStream, this.server);
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

    protected abstract void InitializeFormattersAndHandlers();

    protected virtual JsonRpc CreateJsonRpcWithTargetObject<T>(Stream stream, T targetObject, JsonRpcTargetOptions? options = null)
        where T : notnull
        => this.CreateJsonRpcWithTargetObject(new HeaderDelimitedMessageHandler(stream), targetObject, options);

    protected virtual JsonRpc CreateJsonRpcWithTargetObject<T>(IJsonRpcMessageHandler messageHandler, T targetObject, JsonRpcTargetOptions? options = null)
        where T : notnull
    {
        JsonRpc jsonRpc = new(messageHandler);
        jsonRpc.AddLocalRpcTarget(targetObject, options);
        return jsonRpc;
    }

    private void ReinitializeRpcWithoutListening()
    {
        var streams = Nerdbank.FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.InitializeFormattersAndHandlers();

        this.serverRpc = this.CreateJsonRpcWithTargetObject(this.serverMessageHandler, this.server);
        this.clientRpc = this.CreateJsonRpcWithTargetObject(this.clientMessageHandler, this.client);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
    }

    [DataContract]
    [GenerateShape]
    public partial class Fruit : IFruit
    {
        [ConstructorShape]
        internal Fruit(string name)
        {
            this.Name = name;
        }

        [DataMember]
        public string Name { get; }
    }

    [DataContract]
    [GenerateShape]
    protected internal partial class CustomEventArgs : EventArgs
    {
        [DataMember]
        public int Seeds { get; set; }
    }

    [DataContract]
    protected internal class MessageEventArgs<T> : EventArgs
        where T : class
    {
        [DataMember]
        public T? Message { get; set; }
    }

    protected internal class Client
    {
        internal Action<EventArgs>? ServerEventRaised { get; set; }

        internal Action<EventArgs>? PublicStaticServerEventRaised { get; set; }

        internal Action<CustomEventArgs>? GenericServerEventRaised { get; set; }

        internal Action<MessageEventArgs<string>>? ServerEventWithCustomGenericDelegateAndArgsRaised { get; set; }

        internal Action<IFruit>? ServerIFruitEventRaised { get; set; }

        public void ServerEvent(EventArgs args) => this.ServerEventRaised?.Invoke(args);

        public void PublicStaticServerEvent(EventArgs args) => this.PublicStaticServerEventRaised?.Invoke(args);

        public void ServerEventWithCustomArgs(CustomEventArgs args) => this.GenericServerEventRaised?.Invoke(args);

        public void ServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.ServerEventWithCustomGenericDelegateAndArgsRaised?.Invoke(args);

        public void IFruitEvent(IFruit args) => this.ServerIFruitEventRaised?.Invoke(args);
    }

    protected internal abstract class ServerBase
    {
        public abstract event EventHandler? AbstractBaseEvent;
    }

    protected internal class Server : ServerBase, IServerDerived
    {
        private EventHandler? explicitInterfaceImplementationEvent;

        private EventHandler? abstractBaseEvent;

        public delegate void MessageReceivedEventHandler<T>(object sender, MessageEventArgs<T> args)
            where T : class;

        public static event EventHandler? PublicStaticServerEvent;

        public event EventHandler? ServerEvent;

        public event EventHandler<CustomEventArgs>? ServerEventWithCustomArgs;

        public event MessageReceivedEventHandler<string>? ServerEventWithCustomGenericDelegateAndArgs;

        public event EventHandler? InterfaceEvent;

        public event EventHandler<IFruit>? IFruitEvent;

        public event EventHandler? DerivedInterfaceEvent;

        public override event EventHandler? AbstractBaseEvent
        {
            add => this.abstractBaseEvent += value;
            remove => this.abstractBaseEvent -= value;
        }

        private static event EventHandler? PrivateStaticServerEvent;

        event EventHandler IServer.ExplicitInterfaceImplementation_Event
        {
            add => this.explicitInterfaceImplementationEvent += value;
            remove => this.explicitInterfaceImplementationEvent -= value;
        }

        private event EventHandler? PrivateServerEvent;

        internal EventHandler? ServerEventAccessor => this.ServerEvent;

        internal EventHandler<CustomEventArgs>? ServerEventWithCustomArgsAccessor => this.ServerEventWithCustomArgs;

        internal int HandlersAttachedToAbstractBaseEvent => this.abstractBaseEvent?.GetInvocationList().Length ?? 0;

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

        public void TriggerDerivedInterfaceEvent(EventArgs args) => this.DerivedInterfaceEvent?.Invoke(this, args);

        public void TriggerIFruitEvent(IFruit args) => this.IFruitEvent?.Invoke(this, args);

        public void TriggerExplicitInterfaceImplementationEvent(EventArgs args) => this.explicitInterfaceImplementationEvent?.Invoke(this, args);

        protected static void OnPrivateStaticServerEvent(EventArgs args) => PrivateStaticServerEvent?.Invoke(null, args);

        protected virtual void OnServerEvent(EventArgs args) => this.ServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomArgs(CustomEventArgs args) => this.ServerEventWithCustomArgs?.Invoke(this, args);

        protected virtual void OnPrivateServerEvent(EventArgs args) => this.PrivateServerEvent?.Invoke(this, args);

        protected virtual void OnServerEventWithCustomGenericDelegateAndArgs(MessageEventArgs<string> args) => this.ServerEventWithCustomGenericDelegateAndArgs?.Invoke(this, args);
    }

    protected class ServerDerived : Server
    {
    }

    protected class ServerWithIncompatibleEvents
    {
        public delegate int MyDelegate(double d);

#pragma warning disable CS0067 // Unused member (It's here for reflection to discover)
        public event MyDelegate? MyEvent;
#pragma warning restore CS0067
    }

    [GenerateShapeFor<MessageEventArgs<string>>] // internal member on Client class.
    private partial class Witness;
}
