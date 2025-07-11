using System.ComponentModel;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Abstract base class for source generated proxies.
/// </summary>
/// <param name="client">The <see cref="JsonRpc"/> instance that this proxy interacts with.</param>
/// <param name="options">Options related to this proxy.</param>
/// <param name="marshaledObjectHandle">The handle for the marshaled object that this proxy represents, if applicable.</param>
/// <param name="onDispose">A delegate to invoke on disposal instead of disposing <paramref name="client"/>.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ProxyBase(JsonRpc client, JsonRpcProxyOptions? options, long? marshaledObjectHandle, Action? onDispose) : IJsonRpcClientProxyInternal
{
    private bool disposed;

    /// <inheritdoc/>
    public event EventHandler<string>? CallingMethod;

    /// <inheritdoc/>
    public event EventHandler<string>? CalledMethod;

    /// <inheritdoc/>
    public JsonRpc JsonRpc => client;

    /// <inheritdoc/>
    public bool IsDisposed => this.disposed || client.IsDisposed;

    /// <inheritdoc/>
    long? IJsonRpcClientProxyInternal.MarshaledObjectHandle => marshaledObjectHandle;

    /// <summary>
    /// Gets options related to this proxy.
    /// </summary>
    protected JsonRpcProxyOptions Options => options ?? JsonRpcProxyOptions.Default;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (onDispose is not null)
        {
            onDispose();
        }
        else if (options?.OnDispose is { } dispose)
        {
            dispose();
        }
        else
        {
            client.Dispose();
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
