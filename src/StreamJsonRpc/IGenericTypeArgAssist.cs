namespace StreamJsonRpc;

internal interface IGenericTypeArgAssist
{
    object? Invoke<T>(object? state = null);
}

internal interface IGenericTypeArgStore
{
    object? Invoke(IGenericTypeArgAssist assist, object? state = null);
}

internal class GenericTypeArgStore<T> : IGenericTypeArgStore
{
    public object? Invoke(IGenericTypeArgAssist assist, object? state = null) => assist.Invoke<T>(state);
}
