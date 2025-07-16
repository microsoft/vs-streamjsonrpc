namespace StreamJsonRpc0003.Violation
{
#pragma warning disable StreamJsonRpc0003
    partial class Wrapper
    {
        #region Violation
        interface IMyService
        {
        }

        void Foo(Stream s)
        {
            IMyService proxy = JsonRpc.Attach<IMyService>(s); // StreamJsonRpc0003 diagnostic here
            using (proxy as IDisposable)
            {
            }
        }
        #endregion
    }
#pragma warning restore StreamJsonRpc0003
}

namespace StreamJsonRpc0003.Fix
{
    partial class Wrapper
    {
        #region Fix
        [RpcContract]
        partial interface IMyService
        {
        }

        void Foo(Stream s)
        {
            IMyService proxy = JsonRpc.Attach<IMyService>(s);
            using (proxy as IDisposable)
            {
            }
        }
        #endregion
    }
}
