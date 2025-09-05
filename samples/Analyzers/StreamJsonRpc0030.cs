namespace StreamJsonRpc0030.Violation
{
#pragma warning disable StreamJsonRpc0030
    #region Violation
    [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonRpcProxy<IMyService>]
    partial interface IMyService
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0030
}

namespace StreamJsonRpc0030.Fix
{
    #region Fix
    [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyService
    {
    }
    #endregion
}
