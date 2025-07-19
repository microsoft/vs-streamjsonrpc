namespace StreamJsonRpc0005.Violation
{
#pragma warning disable StreamJsonRpc0005
    #region Violation
    [RpcMarshalable]
    partial interface IMyObject
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0005
}

namespace StreamJsonRpc0005.Fix1
{
    #region Fix1
    [RpcMarshalable]
    partial interface IMyObject : IDisposable
    {
    }
    #endregion
}

namespace StreamJsonRpc0005.Fix2
{
    #region Fix2
    [RpcMarshalable(CallScopedLifetime = true)]
    partial interface IMyObject
    {
    }
    #endregion
}
