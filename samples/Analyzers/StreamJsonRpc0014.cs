namespace StreamJsonRpc0014.Violation
{
#pragma warning disable StreamJsonRpc0014
    #region Violation
    [JsonRpcContract]
    partial interface IMyService
    {
        Task AddAsync(int item, CancellationToken cancellationToken, int before); // StreamJsonRpc0014
    }
    #endregion
#pragma warning restore StreamJsonRpc0014
}

namespace StreamJsonRpc0014.Fix
{
    #region Fix
    [JsonRpcContract]
    partial interface IMyService
    {
        Task AddAsync(int item, int before, CancellationToken cancellationToken);
    }
    #endregion
}
