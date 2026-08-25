namespace StreamJsonRpc0009.Violation
{
#pragma warning disable StreamJsonRpc0009
    #region Violation
    [RpcMarshalable(IsOptional = true), TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject : IDisposable
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0009
}

namespace StreamJsonRpc0009.Fix
{
    #region Fix
    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject : IDisposable
    {
    }
    #endregion
}
