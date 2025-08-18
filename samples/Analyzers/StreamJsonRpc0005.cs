namespace StreamJsonRpc0005.Violation
{
#pragma warning disable StreamJsonRpc0005
    #region Violation
    [RpcMarshalable, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0005
}

namespace StreamJsonRpc0005.Fix1
{
    #region Fix1
    [RpcMarshalable, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject : IDisposable
    {
    }
    #endregion
}

namespace StreamJsonRpc0005.Fix2
{
    #region Fix2
    [RpcMarshalable(CallScopedLifetime = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject
    {
    }
    #endregion
}
