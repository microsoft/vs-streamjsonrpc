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

        [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
        internal partial interface ICalculator
        {
            Task<int> AddAsync(int a, int b);
        }
        #endregion

        #region MultipleProxiesInterfaces
        [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
        internal partial interface IAdvancedCalculator
        {
            Task<int> MultiplyAsync(int a, int b);
        }
        #endregion

        static async Task MultipleProxiesOption1(Stream rpcStream)
        {
            #region MultipleProxiesOption1
            ICalculator calculator = JsonRpc.Attach<ICalculator>(rpcStream);
            JsonRpc rpc = ((IJsonRpcClientProxy)calculator).JsonRpc;
            IAdvancedCalculator advancedCalculator = rpc.Attach<IAdvancedCalculator>();
            using (calculator as IDisposable)
            using (advancedCalculator as IDisposable)
            {
                int sum = await calculator.AddAsync(2, 5);
                int product = await advancedCalculator.MultiplyAsync(2, 5);
            }
            #endregion
        }

        static async Task MultipleProxiesOption2(Stream rpcStream)
        {
            #region MultipleProxiesOption2
            using JsonRpc rpc = JsonRpc.Attach(rpcStream);
            ICalculator calculator = rpc.Attach<ICalculator>();
            IAdvancedCalculator advancedCalculator = rpc.Attach<IAdvancedCalculator>();
            using (calculator as IDisposable)
            using (advancedCalculator as IDisposable)
            {
                int sum = await calculator.AddAsync(2, 5);
                int product = await advancedCalculator.MultiplyAsync(2, 5);
            }
            #endregion
        }

        static async Task MultipleProxiesOption3(Stream rpcStream)
        {
            #region MultipleProxiesOption3
            using JsonRpc rpc = JsonRpc.Attach(rpcStream);
            object combinedProxy = rpc.Attach([typeof(ICalculator), typeof(IAdvancedCalculator)], options: null);
            ICalculator calculator = (ICalculator)combinedProxy;
            IAdvancedCalculator advancedCalculator = (IAdvancedCalculator)combinedProxy;
            using (combinedProxy as IDisposable)
            {
                int sum = await calculator.AddAsync(2, 5);
                int product = await advancedCalculator.MultiplyAsync(2, 5);
            }
            #endregion
        }

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
