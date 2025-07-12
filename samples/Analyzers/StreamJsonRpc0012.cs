namespace StreamJsonRpc0012.Violation
{
#pragma warning disable StreamJsonRpc0012
    #region Violation
    [RpcContract]
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
    [RpcContract]
    interface IMyService
    {
        Task<int> GetCountAsync();
    }
    #endregion
}
