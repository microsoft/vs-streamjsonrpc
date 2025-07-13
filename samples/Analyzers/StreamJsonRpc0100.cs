namespace StreamJsonRpc0100.Violation
{
#pragma warning disable StreamJsonRpc0100
    #region Violation
    [AllowAddingMembersLater]
    partial interface IMyService
    {
    }

    delegate void MyDelegate();
    delegate void MyDelegateInt(int value);
    #endregion
#pragma warning restore StreamJsonRpc0100
}

namespace StreamJsonRpc0100.Fix
{
    #region Fix
    [RpcContract, AllowAddingMembersLater]
    partial interface IMyService
    {
    }
    #endregion
}
