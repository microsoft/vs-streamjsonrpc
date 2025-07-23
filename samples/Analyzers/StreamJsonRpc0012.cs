// Suppress the proxy generation because this interface is invalid.
using JsonRpcContractAttribute = Samples.Analyzers.NoProxy.JsonRpcContractAttribute;

namespace StreamJsonRpc0012.Violation
{
#pragma warning disable StreamJsonRpc0012
    #region Violation
    [JsonRpcContract]
    interface IMyService
    {
        int Count { get; } // StreamJsonRpc0012
    }
    #endregion
#pragma warning restore StreamJsonRpc0012
}

namespace StreamJsonRpc0012.Fix
{
    #region Fix
    [JsonRpcContract]
    interface IMyService
    {
        Task<int> GetCountAsync();
    }
    #endregion
}
