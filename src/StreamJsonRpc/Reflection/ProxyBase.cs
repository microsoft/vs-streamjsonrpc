using System.ComponentModel;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Abstract base class for source generated proxies.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ProxyBase : IJsonRpcClientProxyInternal
{
    private readonly JsonRpc client;
    private readonly JsonRpcProxyOptions? options;
    private readonly long? marshaledObjectHandle;
    private readonly Action? onDispose;
    private readonly ReadOnlyMemory<Type>? requestedInterfaces;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyBase"/> class.
    /// </summary>
    /// <param name="client">The <see cref="JsonRpc"/> instance that this proxy interacts with.</param>
    /// <param name="options">Options related to this proxy.</param>
    /// <param name="marshaledObjectHandle">The handle for the marshaled object that this proxy represents, if applicable.</param>
    /// <param name="onDispose">A delegate to invoke on disposal instead of disposing <paramref name="client"/>.</param>
    /// <param name="requestedInterfaces">The set of interfaces the client requested for this proxy.</param>
    public ProxyBase(JsonRpc client, JsonRpcProxyOptions? options, long? marshaledObjectHandle, Action? onDispose, ReadOnlyMemory<Type>? requestedInterfaces)
    {
        if (options?.ProxyStyle is JsonRpcProxyOptions.ProxyImplementation.AlwaysDynamic)
        {
            throw new NotSupportedException(Resources.InterceptedProxiesRequestCannotProvideDynamicProxies);
        }

        this.client = client;
        this.options = options;
        this.marshaledObjectHandle = marshaledObjectHandle;
        this.onDispose = onDispose;
        this.requestedInterfaces = requestedInterfaces;
    }

    /// <inheritdoc/>
    public event EventHandler<string>? CallingMethod;

    /// <inheritdoc/>
    public event EventHandler<string>? CalledMethod;

    /// <inheritdoc/>
    public JsonRpc JsonRpc => this.client;

    /// <inheritdoc/>
    public bool IsDisposed => this.disposed || this.client.IsDisposed;

    /// <inheritdoc/>
    long? IJsonRpcClientProxyInternal.MarshaledObjectHandle => this.marshaledObjectHandle;

    /// <summary>
    /// Gets options related to this proxy.
    /// </summary>
    protected JsonRpcProxyOptions Options => this.options ?? JsonRpcProxyOptions.Default;

    /// <inheritdoc/>
    public bool IsInterfaceIntentionallyImplemented(Type contract)
    {
        Requires.NotNull(contract);

        // If the type check fails, then the contract is definitely not implemented.
        if (!contract.IsAssignableFrom(this.GetType()))
        {
            return false;
        }

        if (!this.requestedInterfaces.HasValue || this.options?.AcceptProxyWithExtraInterfaces is not true)
        {
            // There's no chance this proxy implements too many interfaces.
            return true;
        }

        foreach (Type iface in this.requestedInterfaces.Value.Span)
        {
            if (iface == contract)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.onDispose is not null)
        {
            this.onDispose();
        }
        else
        {
            this.client.Dispose();
        }
    }

    /// <summary>
    /// Invokes the <see cref="CallingMethod"/> event.
    /// </summary>
    /// <param name="method">The name of the method to be invoked.</param>
    protected void OnCallingMethod(string method) => this.CallingMethod?.Invoke(this, method);

    /// <summary>
    /// Invokes the <see cref="CalledMethod"/> event.
    /// </summary>
    /// <param name="method">The name of the method that was invoked.</param>
    protected void OnCalledMethod(string method) => this.CalledMethod?.Invoke(this, method);
}
