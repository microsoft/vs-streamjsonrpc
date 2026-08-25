// Suppress PolyType so it doesn't complain too.
using GenerateShapeAttribute = Samples.Analyzers.NoProxy.GenerateShapeAttribute;
// Suppress the proxy generation because this interface is invalid.
using JsonRpcContractAttribute = Samples.Analyzers.NoProxy.JsonRpcContractAttribute;

namespace StreamJsonRpc0013.Violation
{
#pragma warning disable StreamJsonRpc0013
    #region Violation
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyService
    {
        Task AddThis<T>(T item); // StreamJsonRpc0013
    }
    #endregion
#pragma warning restore StreamJsonRpc0013
}

namespace StreamJsonRpc0013.Fix
{
    #region Fix
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyService
    {
        Task AddThis(MyItem item);
    }
    #endregion

    class MyItem;
}
