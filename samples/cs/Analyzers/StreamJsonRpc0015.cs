// Suppress PolyType so it doesn't complain too.
using GenerateShapeAttribute = Samples.Analyzers.NoProxy.GenerateShapeAttribute;
// Suppress the proxy generation because this interface is invalid.
using JsonRpcContractAttribute = Samples.Analyzers.NoProxy.JsonRpcContractAttribute;

namespace StreamJsonRpc0015.Violation
{
#pragma warning disable StreamJsonRpc0015, PT0004
    #region Violation
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyService<T>
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0015, PT0004
}

namespace StreamJsonRpc0015.Fix
{
    #region Fix
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyService
    {
    }
    #endregion
}
