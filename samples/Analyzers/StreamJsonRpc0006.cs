namespace StreamJsonRpc0006.Violation
{
#pragma warning disable StreamJsonRpc0006
    #region Violation
    [JsonRpcProxyInterfaceGroup(typeof(IMyService2))]
    partial interface IMyService
    {
    }

    partial interface IMyService2
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0006
}

namespace StreamJsonRpc0006.Fix
{
    #region Fix
    [JsonRpcContract]
    [JsonRpcProxyInterfaceGroup(typeof(IMyService2))]
    partial interface IMyService
    {
    }

    [JsonRpcContract]
    partial interface IMyService2
    {
    }
    #endregion
}
