// Suppress the proxy generation because this interface is invalid.
using JsonRpcContractAttribute = Samples.Analyzers.NoProxy.JsonRpcContractAttribute;

namespace StreamJsonRpc0015.Violation
{
#pragma warning disable StreamJsonRpc0015
    #region Violation
    [JsonRpcContract]
    partial interface IMyService<T>
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0015
}

namespace StreamJsonRpc0015.Fix
{
    #region Fix
    [JsonRpcContract]
    partial interface IMyService
    {
    }
    #endregion
}
