namespace Proxies
{
    partial class ClientSample
    {
        static async Task WithoutProxies(Stream rpcStream)
        {
            #region WithoutProxies
            using JsonRpc jsonRpc = JsonRpc.Attach(rpcStream);
            int sum = await jsonRpc.InvokeAsync<int>("AddAsync", 2, 5);
            #endregion
        }

        #region WithProxies
        static async Task WithProxies(Stream rpcStream)
        {
            ICalculator calc = JsonRpc.Attach<ICalculator>(rpcStream);
            using (calc as IDisposable)
            {
                int sum = await calc.AddAsync(2, 5);
            }
        }

        [RpcContract]
        internal partial interface ICalculator
        {
            Task<int> AddAsync(int a, int b);
        }
        #endregion

        partial class ServerWithoutInterface
        {
            #region ServerWithoutInterface
            class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            #endregion
        }

        partial class ServerWithInterface
        {
            #region ServerWithInterface
            class Calculator : ICalculator
            {
                public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);
            }
            #endregion
        }
    }
}
