// Suppress the proxy generation because this interface is invalid.
using JsonRpcContractAttribute = Samples.Analyzers.NoProxy.JsonRpcContractAttribute;

namespace StreamJsonRpc0013.Violation
{
#pragma warning disable StreamJsonRpc0013
    #region Violation
    [JsonRpcContract]
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
    [JsonRpcContract]
    partial interface IMyService
    {
        Task AddThis(MyItem item);
    }
    #endregion

    class MyItem;
}
