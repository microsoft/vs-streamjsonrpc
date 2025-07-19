// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Options that may customize how a generated client proxy object calls into a <see cref="JsonRpc"/> instance.
/// </summary>
public class JsonRpcProxyOptions
{
    /// <summary>
    /// Backing field for the <see cref="MethodNameTransform"/> property.
    /// </summary>
    private Func<string, string> methodNameTransform = n => n;

    /// <summary>
    /// Backing field for the <see cref="EventNameTransform"/> property.
    /// </summary>
    private Func<string, string> eventNameTransform = n => n;

    /// <summary>
    /// Backing field for the <see cref="OnDispose"/> property.
    /// </summary>
    private Action? onDispose;

    /// <summary>
    /// Backing field for the <see cref="ServerRequiresNamedArguments"/> property.
    /// </summary>
    private bool serverRequiresNamedArguments;

    /// <summary>
    /// Backing field for the <see cref="OnProxyConstructed"/> property.
    /// </summary>
    private Action<IJsonRpcClientProxyInternal>? onProxyConstructed;

    /// <summary>
    /// Backing field for the <see cref="AcceptProxyWithExtraInterfaces"/> property.
    /// </summary>
    private bool acceptProxyWithExtraInterfaces;

    /// <summary>
    /// Backing field for the <see cref="ProxyStyle"/> property.
    /// </summary>
    private ProxyImplementation proxyImpl = ProxyImplementation.PreferSourceGenerated;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonRpcProxyOptions"/> class.
    /// </summary>
    public JsonRpcProxyOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonRpcProxyOptions"/> class
    /// with properties initialized from another instance.
    /// </summary>
    /// <param name="copyFrom">The options to copy values from.</param>
    public JsonRpcProxyOptions(JsonRpcProxyOptions copyFrom)
    {
        Requires.NotNull(copyFrom, nameof(copyFrom));

        this.MethodNameTransform = copyFrom.MethodNameTransform;
        this.EventNameTransform = copyFrom.EventNameTransform;
        this.ServerRequiresNamedArguments = copyFrom.ServerRequiresNamedArguments;
        this.AcceptProxyWithExtraInterfaces = copyFrom.AcceptProxyWithExtraInterfaces;
        this.OnDispose = copyFrom.OnDispose;
        this.OnProxyConstructed = copyFrom.OnProxyConstructed;
    }

    /// <summary>
    /// Describes the source generated proxy that is acceptable to the client.
    /// </summary>
    public enum ProxyImplementation
    {
        /// <summary>
        /// Always use a dynamically generated proxy.
        /// </summary>
        AlwaysDynamic,

        /// <summary>
        /// Use a source generated proxy if available, otherwise fall back to a dynamically generated proxy.
        /// An exception will not be thrown if no source generated proxy is available and the runtime does
        /// not support dynamic code generation.
        /// </summary>
        PreferSourceGenerated,

        /// <summary>
        /// Throw an exception if no source generated proxy is available.
        /// </summary>
        AlwaysSourceGenerated,
    }

    /// <summary>
    /// Gets an instance with default properties.
    /// </summary>
    /// <remarks>
    /// Callers should *not* mutate properties on this instance since it is shared.
    /// </remarks>
    public static JsonRpcProxyOptions Default { get; } = new JsonRpcProxyOptions() { IsFrozen = true };

    /// <summary>
    /// Gets or sets a function that takes the CLR method name and returns the RPC method name.
    /// This method is useful for adding prefixes to all methods, or making them camelCased.
    /// </summary>
    /// <value>A function, defaulting to a straight pass-through. Never null.</value>
    /// <exception cref="ArgumentNullException">Thrown if set to a null value.</exception>
    public Func<string, string> MethodNameTransform
    {
        get => this.methodNameTransform;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.methodNameTransform = Requires.NotNull(value, nameof(value));
        }
    }

    /// <summary>
    /// Gets or sets a function that takes the CLR event name and returns the RPC event name used in notifications.
    /// This method is useful for adding prefixes to all events, or making them camelCased.
    /// </summary>
    /// <value>A function, defaulting to a straight pass-through. Never null.</value>
    /// <exception cref="ArgumentNullException">Thrown if set to a null value.</exception>
    public Func<string, string> EventNameTransform
    {
        get => this.eventNameTransform;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.eventNameTransform = Requires.NotNull(value, nameof(value));
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the client proxy will pass named arguments (i.e. an args object)
    /// instead of the default positional arguments (i.e. an args array).
    /// </summary>
    public bool ServerRequiresNamedArguments
    {
        get => this.serverRequiresNamedArguments;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.serverRequiresNamedArguments = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether source generated proxies that implement a proper superset of
    /// the client's requested interfaces may be returned.
    /// </summary>
    /// <value>The default value is <see langword="false" />.</value>
    /// <remarks>
    /// <para>
    /// Does not apply when <see cref="ProxyStyle"/> is set to <see cref="ProxyImplementation.AlwaysDynamic"/>.
    /// </para>
    /// <para>
    /// Code that uses the proxy and wants to do feature testing should use <see cref="IJsonRpcClientProxy.As{T}()"/>
    /// instead of conditional casts to avoid false positives when this property is <see langword="true"/>.
    /// </para>
    /// </remarks>
    public bool AcceptProxyWithExtraInterfaces
    {
        get => this.acceptProxyWithExtraInterfaces;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.acceptProxyWithExtraInterfaces = value;
        }
    }

    /// <summary>
    /// Gets or sets the implementation of the proxy to use when generating a client proxy.
    /// </summary>
    /// <value>The default value is <see cref="ProxyImplementation.PreferSourceGenerated"/>.</value>
    public ProxyImplementation ProxyStyle
    {
        get => this.proxyImpl;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.proxyImpl = value;
        }
    }

    /// <summary>
    /// Gets or sets a delegate to invoke when this proxy is disposed.
    /// </summary>
    /// <remarks>
    /// When set, the proxy will *not* automatically dispose of the owning <see cref="JsonRpc"/> instance.
    /// This delegate *may* be called concurrently or more than once if the proxy owner calls <see cref="IDisposable.Dispose"/> concurrently.
    /// </remarks>
    internal Action? OnDispose
    {
        get => this.onDispose;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.onDispose = value;
        }
    }

    /// <summary>
    /// Gets or sets a callback that is invoked whenever a proxy is constructed.
    /// </summary>
    internal Action<IJsonRpcClientProxyInternal>? OnProxyConstructed
    {
        get => this.onProxyConstructed;
        set
        {
            Verify.Operation(!this.IsFrozen, Resources.CannotMutateFrozenInstance);
            this.onProxyConstructed = value;
        }
    }

    private bool IsFrozen { get; init; }
}
