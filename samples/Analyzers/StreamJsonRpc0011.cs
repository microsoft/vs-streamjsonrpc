namespace StreamJsonRpc0011.Violation
{
#pragma warning disable StreamJsonRpc0011
    #region Violation
    [RpcContract]
    interface IMyService
    {
        int Add(int a, int b); // StreamJsonRpc0011
    }
    #endregion
#pragma warning restore StreamJsonRpc0011
}

namespace StreamJsonRpc0011.Fix
{
    #region Fix
    [RpcContract]
    interface IMyService
    {
        Task<int> Add(int a, int b);
    }
    #endregion
}
