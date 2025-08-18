using PolyType;

namespace StreamJsonRpc0008.Violation
{
#pragma warning disable StreamJsonRpc0008
    #region Violation
    [JsonRpcContract]
    partial interface IMyObject
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0008
}

namespace StreamJsonRpc0008.Fix
{
    #region Fix
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject
    {
    }
    #endregion
}
