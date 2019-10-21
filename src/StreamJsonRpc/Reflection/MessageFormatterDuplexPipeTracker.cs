// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.ComponentModel;
    using System.IO.Pipelines;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Assists <see cref="IJsonRpcMessageFormatter"/> implementations with supporting marshaling <see cref="IDuplexPipe"/> over JSON-RPC.
    /// </summary>
    /// <remarks>
    /// Lifetime rules:
    /// * The <see cref="IDuplexPipe"/> always originates on the client and passed as an argument to the server.
    ///   Servers are not allowed to return <see cref="IDuplexPipe"/> to clients because the server would have no feedback if the client dropped it, leaking resources.
    /// * The client can only send an <see cref="IDuplexPipe"/> in a request (that expects a response).
    ///   Notifications would not provide the client with feedback that the server dropped it, leaking resources.
    /// * The client will immediately terminate the <see cref="IDuplexPipe"/> if the server returns ANY error in response to the request, since the server may not be aware of the <see cref="IDuplexPipe"/>.
    /// * The <see cref="IDuplexPipe"/> will NOT be terminated when a successful response is received from the server. Client and server are expected to negotiate the end of the <see cref="IDuplexPipe"/> themselves.
    /// </remarks>
    public class MessageFormatterDuplexPipeTracker : IDisposableObservable
    {
        /// <summary>
        /// A map of outbound request IDs to channels that they included.
        /// </summary>
        private ImmutableDictionary<RequestId, ImmutableList<MultiplexingStream.Channel>> outboundRequestChannelMap = ImmutableDictionary<RequestId, ImmutableList<MultiplexingStream.Channel>>.Empty;

        /// <summary>
        /// A map of inbound request IDs to channels that they included.
        /// </summary>
        private ImmutableDictionary<RequestId, ImmutableList<MultiplexingStream.Channel>> inboundRequestChannelMap = ImmutableDictionary<RequestId, ImmutableList<MultiplexingStream.Channel>>.Empty;

        /// <summary>
        /// The set of channels that have been opened but not yet closed to support outbound requests, keyed by their ID.
        /// </summary>
        private ImmutableDictionary<int, MultiplexingStream.Channel> openOutboundChannels = ImmutableDictionary<int, MultiplexingStream.Channel>.Empty;

        /// <summary>
        /// The set of channels that have been opened but not yet closed to support inbound requests, keyed by their ID.
        /// </summary>
        private ImmutableDictionary<int, MultiplexingStream.Channel> openInboundChannels = ImmutableDictionary<int, MultiplexingStream.Channel>.Empty;

        /// <summary>
        /// Backing field for the <see cref="IDisposableObservable.IsDisposed"/> property.
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageFormatterDuplexPipeTracker"/> class.
        /// </summary>
        public MessageFormatterDuplexPipeTracker()
        {
        }

        /// <summary>
        /// Gets or sets the id of the request currently being serialized for use as a key in <see cref="outboundRequestChannelMap"/>.
        /// </summary>
        public RequestId RequestIdBeingSerialized { get; set; }

        /// <summary>
        /// Gets or sets the ID of the request currently being deserialized for use as a key in <see cref="inboundRequestChannelMap"/>.
        /// </summary>
        public RequestId RequestIdBeingDeserialized { get; set; }

        /// <summary>
        /// Gets or sets the multiplexing stream used to create and accept channels.
        /// </summary>
        /// <remarks>
        /// If this is <c>null</c>, some public methods will throw <see cref="NotSupportedException"/>.
        /// </remarks>
        public MultiplexingStream MultiplexingStream { get; set; }

        /// <inheritdoc/>
        bool IDisposableObservable.IsDisposed => this.isDisposed;

        /// <summary>
        /// Creates a token to represent an <see cref="IDuplexPipe"/> as it is transmitted from the client to an RPC server as a method argument.
        /// </summary>
        /// <param name="duplexPipe">The client pipe that is to be shared with the RPC server. May be null.</param>
        /// <returns>The token to use as the RPC method argument; or <c>null</c> if <paramref name="duplexPipe"/> was <c>null</c>.</returns>
        /// <remarks>
        /// This method should only be called while serializing requests that include an ID (i.e. requests for which we expect a response).
        /// When the response is received, a call should always be made to <see cref="OnResponseReceived(RequestId, bool)"/>.
        /// </remarks>
        /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
        public int? GetToken(IDuplexPipe duplexPipe)
        {
            Verify.NotDisposed(this);

            MultiplexingStream mxstream = this.GetMultiplexingStreamOrThrow();
            if (this.RequestIdBeingSerialized.IsEmpty)
            {
                throw new NotSupportedException(Resources.MarshaledObjectInResponseOrNotificationError);
            }

            if (duplexPipe is null)
            {
                return null;
            }

            MultiplexingStream.Channel channel = mxstream.CreateChannel(new MultiplexingStream.ChannelOptions { ExistingPipe = duplexPipe });

            ImmutableInterlocked.AddOrUpdate(
                ref this.outboundRequestChannelMap,
                this.RequestIdBeingSerialized,
                ImmutableList.Create(channel),
                (key, value) => value.Add(channel));

            // Track open channels to assist in diagnosing abandoned channels.
            ImmutableInterlocked.TryAdd(ref this.openOutboundChannels, channel.Id, channel);
            channel.Completion.ContinueWith(_ => ImmutableInterlocked.TryRemove(ref this.openOutboundChannels, channel.Id, out MultiplexingStream.Channel removedChannel), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Forget();

            return channel.Id;
        }

        /// <summary>
        /// Creates a token to represent a <see cref="PipeReader"/> as it is transmitted from the client to an RPC server as a method argument.
        /// </summary>
        /// <param name="reader">The client pipe that is to be shared with the RPC server. May be null.</param>
        /// <returns>The token to use as the RPC method argument; or <c>null</c> if <paramref name="reader"/> was <c>null</c>.</returns>
        /// <remarks>
        /// This method should only be called while serializing requests that include an ID (i.e. requests for which we expect a response).
        /// When the response is received, a call should always be made to <see cref="OnResponseReceived(RequestId, bool)"/>.
        /// </remarks>
        /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
        public int? GetToken(PipeReader reader) => this.GetToken(reader != null ? new DuplexPipe(reader) : null);

        /// <summary>
        /// Creates a token to represent a <see cref="PipeWriter"/> as it is transmitted from the client to an RPC server as a method argument.
        /// </summary>
        /// <param name="writer">The client pipe that is to be shared with the RPC server. May be null.</param>
        /// <returns>The token to use as the RPC method argument; or <c>null</c> if <paramref name="writer"/> was <c>null</c>.</returns>
        /// <remarks>
        /// This method should only be called while serializing requests that include an ID (i.e. requests for which we expect a response).
        /// When the response is received, a call should always be made to <see cref="OnResponseReceived(RequestId, bool)"/>.
        /// </remarks>
        /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
        public int? GetToken(PipeWriter writer) => this.GetToken(writer != null ? new DuplexPipe(writer) : null);

        /// <summary>
        /// Creates an <see cref="IDuplexPipe"/> from a given token as it is received at the RPC server as a method argument.
        /// </summary>
        /// <param name="token">The method argument, which was originally obtained by the client using the <see cref="GetToken(IDuplexPipe)"/> method.</param>
        /// <returns>The <see cref="IDuplexPipe"/> from the token; or <c>null</c> if <paramref name="token"/> was <c>null</c>.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the token does not match up with an out of band channel offered by the client.</exception>
        /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
        public IDuplexPipe GetPipe(int? token)
        {
            Verify.NotDisposed(this);

            MultiplexingStream mxstream = this.GetMultiplexingStreamOrThrow();
            if (token is null)
            {
                return null;
            }

            if (this.RequestIdBeingDeserialized.IsEmpty)
            {
                throw new NotSupportedException(Resources.MarshaledObjectInResponseOrNotificationError);
            }

            // In the case of multiple overloads, we might be called to convert a channel's token more than once.
            // But we can only accept the channel once, so look up in a dictionary to see if we've already done this.
            if (!this.openInboundChannels.TryGetValue(token.Value, out MultiplexingStream.Channel channel))
            {
                channel = mxstream.AcceptChannel(token.Value);

                ImmutableInterlocked.AddOrUpdate(
                    ref this.inboundRequestChannelMap,
                    this.RequestIdBeingDeserialized,
                    ImmutableList.Create(channel),
                    (key, value) => value.Add(channel));

                // Track open channels to assist in diagnosing abandoned channels and handling multiple overloads.
                ImmutableInterlocked.TryAdd(ref this.openInboundChannels, channel.Id, channel);
                channel.Completion.ContinueWith(_ => ImmutableInterlocked.TryRemove(ref this.openInboundChannels, channel.Id, out MultiplexingStream.Channel removedChannel), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Forget();
            }

            return channel;
        }

        /// <summary>
        /// Creates a <see cref="PipeReader"/> from a given token as it is received at the RPC server as a method argument.
        /// </summary>
        /// <param name="token">The method argument, which was originally obtained by the client using the <see cref="GetToken(IDuplexPipe)"/> method.</param>
        /// <returns>The <see cref="PipeReader"/> from the token; or <c>null</c> if <paramref name="token"/> was <c>null</c>.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the token does not match up with an out of band channel offered by the client.</exception>
        /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
        public PipeReader GetPipeReader(int? token)
        {
            IDuplexPipe duplexPipe = this.GetPipe(token);
            if (duplexPipe != null)
            {
                duplexPipe.Output.Complete();
                return duplexPipe.Input;
            }

            return null;
        }

        /// <summary>
        /// Creates a <see cref="PipeWriter"/> from a given token as it is received at the RPC server as a method argument.
        /// </summary>
        /// <param name="token">The method argument, which was originally obtained by the client using the <see cref="GetToken(IDuplexPipe)"/> method.</param>
        /// <returns>The <see cref="PipeWriter"/> from the token; or <c>null</c> if <paramref name="token"/> was <c>null</c>.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the token does not match up with an out of band channel offered by the client.</exception>
        /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
        public PipeWriter GetPipeWriter(int? token)
        {
            IDuplexPipe duplexPipe = this.GetPipe(token);
            if (duplexPipe != null)
            {
                duplexPipe.Input.Complete();
                return duplexPipe.Output;
            }

            return null;
        }

        /// <summary>
        /// Notifies this tracker when a response to any request is sent
        /// so that appropriate channel and state cleanup can take place.
        /// </summary>
        /// <param name="requestId">The ID of the request for which a response was sent.</param>
        /// <param name="successful">A value indicating whether the response represents a successful result (i.e. a <see cref="JsonRpcResult"/> instead of an <see cref="JsonRpcError"/>).</param>
        public void OnResponseSent(RequestId requestId, bool successful)
        {
            Verify.NotDisposed(this);

            if (ImmutableInterlocked.TryRemove(ref this.inboundRequestChannelMap, requestId, out ImmutableList<MultiplexingStream.Channel> channels))
            {
                // Only kill the channels if the server threw an error.
                // Successful responses make it the responsibility of the client/server to terminate the pipe.
                if (!successful)
                {
                    foreach (MultiplexingStream.Channel channel in channels)
                    {
                        channel.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Notifies this tracker when a response to any request is received
        /// so that appropriate channel and state cleanup can take place.
        /// </summary>
        /// <param name="requestId">The ID of the request for which a response was received.</param>
        /// <param name="successful">A value indicating whether the response represents a successful result (i.e. a <see cref="JsonRpcResult"/> instead of an <see cref="JsonRpcError"/>).</param>
        public void OnResponseReceived(RequestId requestId, bool successful)
        {
            Verify.NotDisposed(this);

            if (ImmutableInterlocked.TryRemove(ref this.outboundRequestChannelMap, requestId, out ImmutableList<MultiplexingStream.Channel> channels))
            {
                // Only kill the channels if the server threw an error.
                // Successful responses make it the responsibility of the client/server to terminate the pipe.
                if (!successful)
                {
                    foreach (MultiplexingStream.Channel channel in channels)
                    {
                        channel.Dispose();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.isDisposed = true;

            // Release memory and shutdown channels that outlived the RPC channel.
            this.outboundRequestChannelMap = this.outboundRequestChannelMap.Clear();
            this.inboundRequestChannelMap = this.inboundRequestChannelMap.Clear();

            ImmutableDictionary<int, MultiplexingStream.Channel> openInboundChannels = Interlocked.Exchange(ref this.openInboundChannels, this.openInboundChannels.Clear());
            foreach (KeyValuePair<int, MultiplexingStream.Channel> entry in openInboundChannels)
            {
                entry.Value.Dispose();
            }

            ImmutableDictionary<int, MultiplexingStream.Channel> openOutboundChannels = Interlocked.Exchange(ref this.openOutboundChannels, this.openOutboundChannels.Clear());
            foreach (KeyValuePair<int, MultiplexingStream.Channel> entry in openOutboundChannels)
            {
                entry.Value.Dispose();
            }
        }

        /// <summary>
        /// Throws <see cref="NotSupportedException"/> if <see cref="MultiplexingStream"/> is <c>null</c>.
        /// </summary>
        private MultiplexingStream GetMultiplexingStreamOrThrow()
        {
            return this.MultiplexingStream ?? throw new NotSupportedException(Resources.NotSupportedWithoutMultiplexingStream);
        }
    }
}
