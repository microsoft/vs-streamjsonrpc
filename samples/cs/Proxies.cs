namespace Proxies
{
    partial class ClientSample
    {
        static async Task WithoutProxies(Stream rpcStream)
        {
            #region WithoutProxies
            HeaderDelimitedMessageHandler handler = new(rpcStream, new SystemTextJsonFormatter());
            using JsonRpc jsonRpc = JsonRpc.Attach(handler);
            int sum = await jsonRpc.InvokeAsync<int>("AddAsync", 2, 5);
            #endregion
        }

        #region WithProxies
        static async Task WithProxies(Stream rpcStream)
        {
            HeaderDelimitedMessageHandler handler = new(rpcStream, new SystemTextJsonFormatter());
            ICalculator calc = JsonRpc.Attach<ICalculator>(handler);
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
            #region MultipleProxiesPattern3
            HeaderDelimitedMessageHandler handler = new(rpcStream, new SystemTextJsonFormatter());
            ICalculator calculator = JsonRpc.Attach<ICalculator>(handler);
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
            #region MultipleProxiesPattern2
            HeaderDelimitedMessageHandler handler = new(rpcStream, new SystemTextJsonFormatter());
            using JsonRpc rpc = JsonRpc.Attach(handler);
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
            #region MultipleProxiesPattern1
            HeaderDelimitedMessageHandler handler = new(rpcStream, new SystemTextJsonFormatter());
            using JsonRpc rpc = JsonRpc.Attach(handler);
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
