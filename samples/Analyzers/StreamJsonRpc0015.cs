// Suppress the proxy generation because this interface is invalid.
using RpcContractAttribute = Samples.Analyzers.NoProxy.RpcContractAttribute;

namespace StreamJsonRpc0015.Violation
{
#pragma warning disable StreamJsonRpc0015
    #region Violation
    [RpcContract]
    interface IMyService<T>
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0015
}

namespace StreamJsonRpc0015.Fix
{
    #region Fix
    [RpcContract]
    interface IMyService
    {
    }
    #endregion
}
