namespace StreamJsonRpc0010.Violation
{
#pragma warning disable StreamJsonRpc0010
    #region Violation
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface ICalculator
    {
        Task<int> CalculateAsync(int value, string format);

        Task<int> CalculateAsync(int value, double rate, CancellationToken cancellationToken);
    }
    #endregion
#pragma warning restore StreamJsonRpc0010
}

namespace StreamJsonRpc0010.Fix
{
    #region Fix
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface ICalculator
    {
        Task<int> CalculateAsync(int value, string format);

        [JsonRpcMethod("CalculateFromRate")]
        Task<int> CalculateAsync(int value, double rate, CancellationToken cancellationToken);
    }
    #endregion
}
