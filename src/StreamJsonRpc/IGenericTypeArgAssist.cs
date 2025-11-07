namespace StreamJsonRpc;

/// <summary>
/// A non-generic interface with a generic method, for use with <see cref="IGenericTypeArgStore"/>
/// so that generic contexts can be preserved and reused later in a non-generic context.
/// </summary>
internal interface IGenericTypeArgAssist
{
    /// <summary>
    /// Invokes whatever functionality the implementation provides, using the specified generic type argument.
    /// </summary>
    /// <typeparam name="T">A generic type argument, whose semantics are up to the implementation.</typeparam>
    /// <param name="state">Optional state that the caller may have provided.</param>
    /// <returns>An arbitrary result, up to the implementation.</returns>
    object? Invoke<T>(object? state = null);
}

/// <summary>
/// A non-generic interface allowing invocation of a generic method with a type argument known only at runtime.
/// </summary>
/// <seealso cref="GenericTypeArgStore{T}"/>
internal interface IGenericTypeArgStore
{
    /// <summary>
    /// Invokes the generic <see cref="IGenericTypeArgAssist.Invoke{T}(object?)"/> method.
    /// </summary>
    /// <param name="assist">An implementation of <see cref="IGenericTypeArgAssist"/>.</param>
    /// <param name="state">Optional state that the caller may have provided.</param>
    /// <returns>An arbitrary result, up to the implementation.</returns>
    object? Invoke(IGenericTypeArgAssist assist, object? state = null);
}

/// <summary>
/// A generic implementation of <see cref="IGenericTypeArgStore"/>
/// that can be created while in a generic context, so that a non-generic method can invoke another generic method later.
/// </summary>
/// <typeparam name="T">The generic type argument that must be supplied later.</typeparam>
internal class GenericTypeArgStore<T> : IGenericTypeArgStore
{
    /// <inheritdoc/>
    public object? Invoke(IGenericTypeArgAssist assist, object? state = null) => assist.Invoke<T>(state);
}
