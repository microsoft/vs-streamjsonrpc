namespace StreamJsonRpc0002.Violation
{
#pragma warning disable StreamJsonRpc0002
    #region Violation
    internal class Wrapper
    {
        [RpcContract]
        private interface IMyService
        {
            Task<int> Add(int a, int b); // StreamJsonRpc0002
        }
    }
    #endregion
#pragma warning restore StreamJsonRpc0002
}

namespace StreamJsonRpc0002.Fix
{
    #region Fix
    internal class Wrapper
    {
        [RpcContract]
        internal interface IMyService
        {
            Task<int> Add(int a, int b);
        }
    }
    #endregion
}
