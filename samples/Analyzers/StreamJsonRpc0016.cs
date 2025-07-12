// Suppress the proxy generation because this interface is invalid.
using RpcContractAttribute = Samples.Analyzers.NoProxy.RpcContractAttribute;

namespace StreamJsonRpc0016.Violation
{
#pragma warning disable StreamJsonRpc0016
    #region Violation
    [RpcContract]
    partial interface IMyService
    {
        event MyDelegate MyEvent; // StreamJsonRpc0016
        event MyDelegateInt MyIntEvent; // StreamJsonRpc0016
    }

    delegate void MyDelegate();
    delegate void MyDelegateInt(int value);
    #endregion
#pragma warning restore StreamJsonRpc0016
}

namespace StreamJsonRpc0016.Fix
{
    #region Fix
    [RpcContract]
    partial interface IMyService
    {
        event EventHandler MyEvent;
        event EventHandler<int> MyIntEvent;
    }
    #endregion
}
