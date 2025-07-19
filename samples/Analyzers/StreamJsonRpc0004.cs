namespace StreamJsonRpc0004.Violation
{
#pragma warning disable StreamJsonRpc0004
    partial class Wrapper
    {
        #region Violation
        internal class MyService
        {
        }

        void Foo(Stream s)
        {
            MyService proxy = JsonRpc.Attach<MyService>(s); // StreamJsonRpc0004 diagnostic here
            using (proxy as IDisposable)
            {
            }
        }
        #endregion
    }
#pragma warning restore StreamJsonRpc0004
}

namespace StreamJsonRpc0004.Fix
{
    partial class Wrapper
    {
        #region Fix
        [JsonRpcContract]
        internal partial interface IMyService
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
