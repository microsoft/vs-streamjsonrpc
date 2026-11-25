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
            HeaderDelimitedMessageHandler handler = new(s, new SystemTextJsonFormatter());
            MyService proxy = JsonRpc.Attach<MyService>(handler); // StreamJsonRpc0004 diagnostic here
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
        [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
        internal partial interface IMyService
        {
        }

        void Foo(Stream s)
        {
            HeaderDelimitedMessageHandler handler = new(s, new SystemTextJsonFormatter());
            IMyService proxy = JsonRpc.Attach<IMyService>(handler);
            using (proxy as IDisposable)
            {
            }
        }
        #endregion
    }
}
