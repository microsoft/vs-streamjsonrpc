namespace StreamJsonRpc0050.Violation
{
#pragma warning disable StreamJsonRpc0050
    #region Violation
    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalable]
    [RpcMarshalableOptionalInterface(1, typeof(IMyObject2))]
    partial interface IMyObject : IDisposable
    {
    }

    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalable(IsOptional = true)]
    partial interface IMyObject2 : IDisposable
    {
    }

    class OneWay
    {
        bool IsOperator(IMyObject o) => o is IMyObject2;
        IMyObject2? AsOperator(IMyObject o) => o as IMyObject2;
        IMyObject2 CastOperator(IMyObject o) => (IMyObject2)o;
    }
    #endregion
#pragma warning restore StreamJsonRpc0050
}

namespace StreamJsonRpc0050.Fix
{
    #region Fix
    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalable]
    [RpcMarshalableOptionalInterface(1, typeof(IMyObject2))]
    partial interface IMyObject : IDisposable
    {
    }

    [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalable(IsOptional = true)]
    partial interface IMyObject2 : IDisposable
    {
    }

    class OneWay
    {
        bool IsOperator(IMyObject o) => o.Is(typeof(IMyObject2));
        IMyObject2? AsOperator(IMyObject o) => o.As<IMyObject2>();
        IMyObject2 CastOperator(IMyObject o) => o.As<IMyObject2>() ?? throw new InvalidCastException();
    }
    #endregion
}
