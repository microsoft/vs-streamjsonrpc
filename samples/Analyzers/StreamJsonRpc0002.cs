#pragma warning disable StreamJsonRpc0008

namespace StreamJsonRpc0002.Violation
{
#pragma warning disable StreamJsonRpc0002
    #region Violation
    internal class Wrapper
    {
        [JsonRpcContract]
        internal interface IMyService
        {
        }
    }
    #endregion
#pragma warning restore StreamJsonRpc0002
}

namespace StreamJsonRpc0002.Fix
{
    #region Fix
    internal partial class Wrapper
    {
        [JsonRpcContract]
        internal partial interface IMyService
        {
        }
    }
    #endregion
}
