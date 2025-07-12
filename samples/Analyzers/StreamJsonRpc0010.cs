namespace StreamJsonRpc0010.Violation
{
#pragma warning disable StreamJsonRpc0010
    #region Violation
    internal partial class Wrapper
    {
        [RpcContract]
        private partial interface IMyService
        {
            Task<int> Add(int a, int b); // StreamJsonRpc0010
        }
    }
    #endregion
#pragma warning restore StreamJsonRpc0010
}

namespace StreamJsonRpc0010.Fix
{
    #region Fix
    internal partial class Wrapper
    {
        [RpcContract]
        internal partial interface IMyService
        {
            Task<int> Add(int a, int b);
        }
    }
    #endregion
}
