namespace StreamJsonRpc0031.Violation
{
#pragma warning disable StreamJsonRpc0031
    #region Violation
    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonRpcProxy<int>]
    partial interface IMyService<T> : IDisposable
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0031
}

namespace StreamJsonRpc0031.Fix
{
    #region Fix
    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonRpcProxy<IMyService<int>>]
    partial interface IMyService<T> : IDisposable
    {
    }
    #endregion
}
