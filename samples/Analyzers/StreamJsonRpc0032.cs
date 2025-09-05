namespace StreamJsonRpc0032.Violation
{
#pragma warning disable StreamJsonRpc0032
    #region Violation
    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonRpcProxy<IMyService<int>>]
    partial interface IMyService<T> : IDisposable
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0032
}

namespace StreamJsonRpc0032.Fix
{
    #region Fix
    [RpcMarshalable]
    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonRpcProxy<IMyService<int>>]
    partial interface IMyService<T> : IDisposable
    {
    }
    #endregion
}
