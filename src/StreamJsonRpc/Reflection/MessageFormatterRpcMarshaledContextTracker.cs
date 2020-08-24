// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.Serialization;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using static System.FormattableString;

    /// <summary>
    /// Tracks objects that get marshaled using the general marshaling protocol.
    /// </summary>
    internal class MessageFormatterRpcMarshaledContextTracker
    {
        private readonly Dictionary<long, (IRpcMarshaledContext<object> Context, IDisposable Revert)> marshaledObjects = new Dictionary<long, (IRpcMarshaledContext<object> Context, IDisposable Revert)>();
        private readonly JsonRpc jsonRpc;
        private readonly IJsonRpcFormatterState formatterState;
        private long nextUniqueHandle;

        /// <summary>
        /// A map of outbound request IDs to handles that are keys in the <see cref="marshaledObjects"/> dictionary.
        /// </summary>
        /// <remarks>
        /// This collection is used to avoid memory leaks when marshaled objects have already been converted to a token for an outbound *request*
        /// and the request ends up not being transmitted for any reason.
        /// It will only contain the data until the request is either aborted or a response is received.
        /// </remarks>
        private ImmutableDictionary<RequestId, ImmutableList<long>> outboundRequestIdMarshalMap = ImmutableDictionary<RequestId, ImmutableList<long>>.Empty;

        internal MessageFormatterRpcMarshaledContextTracker(JsonRpc jsonRpc, IJsonRpcFormatterState formatterState)
        {
            this.jsonRpc = jsonRpc;
            this.formatterState = formatterState;

            this.jsonRpc.AddLocalRpcMethod("$/releaseMarshaledObject", new Action<long, bool>(this.ReleaseMarshaledObject));

            // We don't offer a way to remove these handlers because this object should has a lifetime closely tied to the JsonRpc object anyway.
            IJsonRpcFormatterCallbacks callbacks = jsonRpc;
            callbacks.RequestTransmissionAborted += (s, e) => this.CleanUpOutboundResources(e.RequestId, successful: false);
            callbacks.ResponseReceived += (s, e) => this.CleanUpOutboundResources(e.RequestId, successful: e.IsSuccessfulResponse);
        }

        /// <summary>
        /// Defines the values of the "__jsonrpc_marshaled" property in the <see cref="MarshalToken"/>.
        /// </summary>
        private enum MarshalMode
        {
            MarshallingProxyBackToOwner = 0,
            MarshallingRealObject = 1,
        }

        /// <summary>
        /// Prepares a local object to be marshaled over the wire.
        /// </summary>
        /// <param name="marshaledContext">The object and metadata to be marshaled.</param>
        /// <returns>A token to be serialized so the remote party can invoke methods on the object within <paramref name="marshaledContext"/>.</returns>
        internal MarshalToken GetToken(IRpcMarshaledContext<object> marshaledContext)
        {
            if (this.formatterState.SerializingMessageWithId.IsEmpty)
            {
                throw new NotSupportedException(Resources.MarshaledObjectInNotificationError);
            }

            long handle = this.nextUniqueHandle++;

            IDisposable? revert = this.jsonRpc.AddLocalRpcTargetInternal(
                marshaledContext.GetType().GenericTypeArguments[0],
                marshaledContext.Proxy,
                new JsonRpcTargetOptions(marshaledContext.JsonRpcTargetOptions)
                {
                    NotifyClientOfEvents = false, // We don't support this yet.
                    MethodNameTransform = mn => Invariant($"$/invokeProxy/{handle}/{marshaledContext.JsonRpcTargetOptions.MethodNameTransform?.Invoke(mn) ?? mn}"),
                },
                requestRevertOption: true);
            Assumes.NotNull(revert);

            lock (this.marshaledObjects)
            {
                this.marshaledObjects.Add(handle, (marshaledContext, revert));
            }

            if (this.formatterState.SerializingRequest)
            {
                ImmutableInterlocked.AddOrUpdate(
                    ref this.outboundRequestIdMarshalMap,
                    this.formatterState.SerializingMessageWithId,
                    ImmutableList.Create(handle),
                    (key, value) => value.Add(handle));
            }

            return new MarshalToken((int)MarshalMode.MarshallingRealObject, handle, lifetime: null);
        }

        /// <summary>
        /// Creates a proxy for a remote object.
        /// </summary>
        /// <typeparam name="T">The interface the proxy must implement.</typeparam>
        /// <param name="token">The token received from the remote party that includes the handle to the remote object.</param>
        /// <param name="options">The options to feed into proxy generation.</param>
        /// <returns>The generated proxy, or <c>null</c> if <paramref name="token"/> is null.</returns>
        [return: NotNullIfNotNull("token")]
        internal T? GetObject<T>(MarshalToken? token, JsonRpcProxyOptions options)
            where T : class
        {
            if (token is null)
            {
                return null;
            }

            if ((MarshalMode)token.Value.Marshaled == MarshalMode.MarshallingProxyBackToOwner)
            {
                throw new NotSupportedException("Receiving marshaled objects back to the owner is not yet supported.");
            }

            if (token.Value.Lifetime == MarshalLifetime.Call)
            {
                throw new NotSupportedException("Receiving marshaled objects scoped to the lifetime of a single RPC request is not yet supported.");
            }

            // CONSIDER: If we ever support arbitrary RPC interfaces, we'd need to consider how events on those interfaces would work.
            return this.jsonRpc.Attach<T>(new JsonRpcProxyOptions(options)
            {
                MethodNameTransform = mn => Invariant($"$/invokeProxy/{token.Value.Handle}/{options.MethodNameTransform(mn)}"),
                OnDispose = delegate
                {
                    this.jsonRpc.NotifyAsync(Invariant($"$/invokeProxy/{token.Value.Handle}/{options.MethodNameTransform(nameof(IDisposable.Dispose))}")).Forget();
                    this.jsonRpc.NotifyWithParameterObjectAsync("$/releaseMarshaledObject", new { handle = token.Value.Handle, ownedBySender = false }).Forget();
                },
            });
        }

        /// <summary>
        /// Releases memory associated with marshaled objects.
        /// </summary>
        /// <param name="handle">The handle to the object as created by the <see cref="GetToken(IRpcMarshaledContext{object})"/> method.</param>
        /// <param name="ownedBySender"><c>true</c> if the <paramref name="handle"/> was created by (and thus the original object owned by) the remote party; <c>false</c> if the token and object was created locally.</param>
        private void ReleaseMarshaledObject(long handle, bool ownedBySender)
        {
            lock (this.marshaledObjects)
            {
                if (this.marshaledObjects.TryGetValue(handle, out (IRpcMarshaledContext<object> Context, IDisposable Revert) info))
                {
                    this.marshaledObjects.Remove(handle);
                    info.Revert.Dispose();

                    // If/when we support exposing the Context object, it may become relevant to dispose of it.
                    ////info.Context.Dispose();
                }
            }
        }

        private void CleanUpOutboundResources(RequestId requestId, bool successful)
        {
            if (ImmutableInterlocked.TryRemove(ref this.outboundRequestIdMarshalMap, requestId, out ImmutableList<long> handles))
            {
                // Only kill the marshaled objects if the server threw an error.
                // Successful responses make it the responsibility of the client/server to terminate the marshaled connection.
                if (!successful)
                {
                    foreach (long handle in handles)
                    {
                        // We use "ownedBySender: false" because the method we're calling is accustomed to the perspective being the "other" party.
                        this.ReleaseMarshaledObject(handle, ownedBySender: false);
                    }
                }
            }
        }

        [DataContract]
        internal struct MarshalToken
        {
            [MessagePack.SerializationConstructor]
#pragma warning disable SA1313 // Parameter names should begin with lower-case letter
            public MarshalToken(int __jsonrpc_marshaled, long handle, string? lifetime)
#pragma warning restore SA1313 // Parameter names should begin with lower-case letter
            {
                this.Marshaled = __jsonrpc_marshaled;
                this.Handle = handle;
                this.Lifetime = lifetime;
            }

            [DataMember(Name = "__jsonrpc_marshaled", IsRequired = true)]
            public int Marshaled { get; }

            [DataMember(Name = "handle", IsRequired = true)]
            public long Handle { get; }

            [DataMember(Name = "lifetime", EmitDefaultValue = false)]
            public string? Lifetime { get; }
        }

        /// <summary>
        /// Defines the values of the "lifetime" property in the <see cref="MarshalToken"/>.
        /// </summary>
        private static class MarshalLifetime
        {
            /// <summary>
            /// The marshaled object may only be invoked until the containing RPC call completes. This value is only allowed when used within a JSON-RPC argument.
            /// No explicit release using `$/releaseMarshaledObject` is required.
            /// </summary>
            internal const string Call = "call";

            /// <summary>
            /// The marshaled object may be invoked until `$/releaseMarshaledObject` releases it. This is the default behavior when the `lifetime` property is omitted.
            /// </summary>
            internal const string Explicit = "explicit";
        }
    }
}
