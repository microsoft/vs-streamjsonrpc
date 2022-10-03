﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

namespace StreamJsonRpc.Reflection;

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
    /// The formatter that owns this tracker.
    /// </summary>
    private readonly IJsonRpcFormatterState formatterState;

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
    private ImmutableDictionary<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel> openOutboundChannels = ImmutableDictionary<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel>.Empty;

    /// <summary>
    /// The set of channels that have been opened but not yet closed to support inbound requests, keyed by their ID.
    /// </summary>
    private ImmutableDictionary<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel> openInboundChannels = ImmutableDictionary<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel>.Empty;

    /// <summary>
    /// Backing field for the <see cref="IDisposableObservable.IsDisposed"/> property.
    /// </summary>
    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageFormatterDuplexPipeTracker"/> class.
    /// </summary>
    /// <param name="jsonRpc">The <see cref="JsonRpc"/> instance that may be used to send or receive RPC messages related to <see cref="IAsyncEnumerable{T}"/>.</param>
    /// <param name="formatterState">The formatter that owns this tracker.</param>
    public MessageFormatterDuplexPipeTracker(JsonRpc jsonRpc, IJsonRpcFormatterState formatterState)
    {
        Requires.NotNull(jsonRpc, nameof(jsonRpc));
        Requires.NotNull(formatterState, nameof(formatterState));

        this.formatterState = formatterState;

        // We don't offer a way to remove these handlers because this object should has a lifetime closely tied to the JsonRpc object anyway.
        IJsonRpcFormatterCallbacks callbacks = jsonRpc;
        callbacks.RequestTransmissionAborted += (s, e) => this.CleanUpOutboundResources(e.RequestId, successful: false);
        callbacks.ResponseReceived += (s, e) => this.CleanUpOutboundResources(e.RequestId, successful: e.IsSuccessfulResponse);
        callbacks.ResponseSent += (s, e) => this.CleanUpInboundResources(e.RequestId, successful: e.IsSuccessfulResponse);
    }

    /// <summary>
    /// Gets or sets the multiplexing stream used to create and accept channels.
    /// </summary>
    /// <remarks>
    /// If this is <see langword="null"/>, some public methods will throw <see cref="NotSupportedException"/>.
    /// </remarks>
    public MultiplexingStream? MultiplexingStream { get; set; }

    /// <inheritdoc/>
    bool IDisposableObservable.IsDisposed => this.isDisposed;

    /// <summary>
    /// Gets the id of the request currently being serialized for use as a key in <see cref="outboundRequestChannelMap"/>.
    /// </summary>
    private RequestId RequestIdBeingSerialized => this.formatterState.SerializingRequest ? this.formatterState.SerializingMessageWithId : default;

    /// <summary>
    /// Gets the ID of the request currently being deserialized for use as a key in <see cref="inboundRequestChannelMap"/>.
    /// </summary>
    private RequestId RequestIdBeingDeserialized => this.formatterState.DeserializingMessageWithId;

    /// <inheritdoc cref="GetULongToken(IDuplexPipe?)"/>
    [return: NotNullIfNotNull("duplexPipe")]
    [Obsolete("Use " + nameof(GetULongToken) + " instead.")]
    public int? GetToken(IDuplexPipe? duplexPipe) => checked((int?)this.GetULongToken(duplexPipe));

    /// <summary>
    /// Creates a token to represent an <see cref="IDuplexPipe"/> as it is transmitted from the client to an RPC server as a method argument.
    /// </summary>
    /// <param name="duplexPipe">The client pipe that is to be shared with the RPC server. May be null.</param>
    /// <returns>The token to use as the RPC method argument; or <see langword="null"/> if <paramref name="duplexPipe"/> was <see langword="null"/>.</returns>
    /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor or when serializing a message without an ID property.</exception>
    [return: NotNullIfNotNull("duplexPipe")]
    public ulong? GetULongToken(IDuplexPipe? duplexPipe)
    {
        Verify.NotDisposed(this);

        MultiplexingStream mxstream = this.GetMultiplexingStreamOrThrow();
        if (this.formatterState.SerializingMessageWithId.IsEmpty)
        {
            duplexPipe?.Output.Complete();
            duplexPipe?.Input.Complete();

            throw new NotSupportedException(Resources.MarshaledObjectInNotificationError);
        }

        if (duplexPipe is null)
        {
            return null;
        }

        MultiplexingStream.Channel channel = mxstream.CreateChannel(new MultiplexingStream.ChannelOptions { ExistingPipe = duplexPipe });

        if (!this.RequestIdBeingSerialized.IsEmpty)
        {
            ImmutableInterlocked.AddOrUpdate(
                ref this.outboundRequestChannelMap,
                this.RequestIdBeingSerialized,
                ImmutableList.Create(channel),
                (key, value) => value.Add(channel));
        }

        // Track open channels to assist in diagnosing abandoned channels.
        ImmutableInterlocked.TryAdd(ref this.openOutboundChannels, channel.QualifiedId, channel);
        channel.Completion.ContinueWith(_ => ImmutableInterlocked.TryRemove(ref this.openOutboundChannels, channel.QualifiedId, out MultiplexingStream.Channel? removedChannel), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Forget();

        return channel.QualifiedId.Id;
    }

    /// <inheritdoc cref="GetULongToken(PipeReader?)"/>
    [return: NotNullIfNotNull("reader")]
    [Obsolete("Use " + nameof(GetULongToken) + " instead.")]
    public int? GetToken(PipeReader? reader) => checked((int?)this.GetULongToken(reader));

    /// <summary>
    /// Creates a token to represent a <see cref="PipeReader"/> as it is transmitted from the client to an RPC server as a method argument.
    /// </summary>
    /// <param name="reader">The client pipe that is to be shared with the RPC server. May be null.</param>
    /// <returns>The token to use as the RPC method argument; or <see langword="null"/> if <paramref name="reader"/> was <see langword="null"/>.</returns>
    /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor or when serializing a message without an ID property.</exception>
    [return: NotNullIfNotNull("reader")]
    public ulong? GetULongToken(PipeReader? reader) => this.GetULongToken(reader is not null ? new DuplexPipe(reader) : null);

    /// <inheritdoc cref="GetULongToken(PipeWriter?)"/>
    [return: NotNullIfNotNull("writer")]
    [Obsolete("Use " + nameof(GetULongToken) + " instead.")]
    public int? GetToken(PipeWriter? writer) => checked((int?)this.GetULongToken(writer));

    /// <summary>
    /// Creates a token to represent a <see cref="PipeWriter"/> as it is transmitted from the client to an RPC server as a method argument.
    /// </summary>
    /// <param name="writer">The client pipe that is to be shared with the RPC server. May be null.</param>
    /// <returns>The token to use as the RPC method argument; or <see langword="null"/> if <paramref name="writer"/> was <see langword="null"/>.</returns>
    /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor or when serializing a message without an ID property.</exception>
    [return: NotNullIfNotNull("writer")]
    public ulong? GetULongToken(PipeWriter? writer) => this.GetULongToken(writer is not null ? new DuplexPipe(writer) : null);

    /// <inheritdoc cref="GetPipe(ulong?)"/>
    [return: NotNullIfNotNull("token")]
    [Obsolete("Use " + nameof(GetPipe) + "(ulong?) instead.")]
    public IDuplexPipe? GetPipe(int? token) => this.GetPipe((ulong?)token);

    /// <summary>
    /// Creates an <see cref="IDuplexPipe"/> from a given token as it is received at the RPC server as a method argument.
    /// </summary>
    /// <param name="token">The method argument, which was originally obtained by the client using the <see cref="GetToken(IDuplexPipe)"/> method.</param>
    /// <returns>The <see cref="IDuplexPipe"/> from the token; or <see langword="null"/> if <paramref name="token"/> was <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the token does not match up with an out of band channel offered by the client.</exception>
    /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
    [return: NotNullIfNotNull("token")]
    public IDuplexPipe? GetPipe(ulong? token)
    {
        Verify.NotDisposed(this);

        MultiplexingStream mxstream = this.GetMultiplexingStreamOrThrow();
        if (token is null)
        {
            return null;
        }

        // In the case of multiple overloads, we might be called to convert a channel's token more than once.
        // But we can only accept the channel once, so look up in a dictionary to see if we've already done this.
        if (!this.openInboundChannels.TryGetValue(new MultiplexingStream.QualifiedChannelId(token.Value, MultiplexingStream.ChannelSource.Remote), out MultiplexingStream.Channel? channel))
        {
            channel = mxstream.AcceptChannel(token.Value);
            if (!this.RequestIdBeingDeserialized.IsEmpty)
            {
                ImmutableInterlocked.AddOrUpdate(
                    ref this.inboundRequestChannelMap,
                    this.RequestIdBeingDeserialized,
                    ImmutableList.Create(channel),
                    (key, value) => value.Add(channel));
            }

            // Track open channels to assist in diagnosing abandoned channels and handling multiple overloads.
            ImmutableInterlocked.TryAdd(ref this.openInboundChannels, channel.QualifiedId, channel);
            channel.Completion.ContinueWith(_ => ImmutableInterlocked.TryRemove(ref this.openInboundChannels, channel.QualifiedId, out MultiplexingStream.Channel? removedChannel), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Forget();
        }

        return channel;
    }

    /// <inheritdoc cref="GetPipeReader(ulong?)"/>
    [return: NotNullIfNotNull("token")]
    [Obsolete("Use " + nameof(GetPipeReader) + "(ulong?) instead.")]
    public PipeReader? GetPipeReader(int? token) => this.GetPipeReader((ulong?)token);

    /// <summary>
    /// Creates a <see cref="PipeReader"/> from a given token as it is received at the RPC server as a method argument.
    /// </summary>
    /// <param name="token">The method argument, which was originally obtained by the client using the <see cref="GetToken(IDuplexPipe)"/> method.</param>
    /// <returns>The <see cref="PipeReader"/> from the token; or <see langword="null"/> if <paramref name="token"/> was <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the token does not match up with an out of band channel offered by the client.</exception>
    /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
    [return: NotNullIfNotNull("token")]
    public PipeReader? GetPipeReader(ulong? token)
    {
        IDuplexPipe? duplexPipe = this.GetPipe(token);
        if (duplexPipe is not null)
        {
            duplexPipe.Output.Complete();
            return duplexPipe.Input;
        }

        return null;
    }

    /// <inheritdoc cref="GetPipeWriter(ulong?)"/>
    [return: NotNullIfNotNull("token")]
    [Obsolete("Use " + nameof(GetPipeWriter) + "(ulong?) instead.")]
    public PipeWriter? GetPipeWriter(int? token) => this.GetPipeWriter((ulong?)token);

    /// <summary>
    /// Creates a <see cref="PipeWriter"/> from a given token as it is received at the RPC server as a method argument.
    /// </summary>
    /// <param name="token">The method argument, which was originally obtained by the client using the <see cref="GetToken(IDuplexPipe)"/> method.</param>
    /// <returns>The <see cref="PipeWriter"/> from the token; or <see langword="null"/> if <paramref name="token"/> was <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the token does not match up with an out of band channel offered by the client.</exception>
    /// <exception cref="NotSupportedException">Thrown if no <see cref="MultiplexingStream"/> was provided to the constructor.</exception>
    [return: NotNullIfNotNull("token")]
    public PipeWriter? GetPipeWriter(ulong? token)
    {
        IDuplexPipe? duplexPipe = this.GetPipe(token);
        if (duplexPipe is not null)
        {
            duplexPipe.Input.Complete();
            return duplexPipe.Output;
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and native resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> if being disposed; <see langword="false"/> if being finalized.</param>
    protected virtual void Dispose(bool disposing)
    {
        this.isDisposed = true;

        // Release memory and shutdown channels that outlived the RPC channel.
        this.outboundRequestChannelMap = this.outboundRequestChannelMap.Clear();
        this.inboundRequestChannelMap = this.inboundRequestChannelMap.Clear();

        ImmutableDictionary<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel> openInboundChannels = Interlocked.Exchange(ref this.openInboundChannels, this.openInboundChannels.Clear());
        foreach (KeyValuePair<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel> entry in openInboundChannels)
        {
            entry.Value.Dispose();
        }

        ImmutableDictionary<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel> openOutboundChannels = Interlocked.Exchange(ref this.openOutboundChannels, this.openOutboundChannels.Clear());
        foreach (KeyValuePair<MultiplexingStream.QualifiedChannelId, MultiplexingStream.Channel> entry in openOutboundChannels)
        {
            entry.Value.Dispose();
        }
    }

    private void CleanUpInboundResources(RequestId requestId, bool successful)
    {
        if (this.isDisposed)
        {
            return;
        }

        if (ImmutableInterlocked.TryRemove(ref this.inboundRequestChannelMap, requestId, out ImmutableList<MultiplexingStream.Channel>? channels))
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

    private void CleanUpOutboundResources(RequestId requestId, bool successful)
    {
        if (this.isDisposed)
        {
            return;
        }

        if (ImmutableInterlocked.TryRemove(ref this.outboundRequestChannelMap, requestId, out ImmutableList<MultiplexingStream.Channel>? channels))
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
    /// Throws <see cref="NotSupportedException"/> if <see cref="MultiplexingStream"/> is <see langword="null"/>.
    /// </summary>
    private MultiplexingStream GetMultiplexingStreamOrThrow()
    {
        return this.MultiplexingStream ?? throw new NotSupportedException(Resources.NotSupportedWithoutMultiplexingStream);
    }
}
