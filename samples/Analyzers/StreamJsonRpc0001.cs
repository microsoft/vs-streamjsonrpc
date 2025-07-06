namespace StreamJsonRpc0001.Violation
{
#pragma warning disable StreamJsonRpc0001
    #region Violation
    [RpcContract]
    public interface IMyService
    {
        int Add(int a, int b); // StreamJsonRpc0001
    }
    #endregion
#pragma warning restore StreamJsonRpc0001
}

namespace StreamJsonRpc0001.Fix
{
    #region Fix
    [RpcContract]
    public interface IMyService
    {
        Task<int> Add(int a, int b);
    }
    #endregion
}
