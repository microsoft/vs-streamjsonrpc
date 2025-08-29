namespace StreamJsonRpc0007.Violation
{
#pragma warning disable StreamJsonRpc0007
    #region Violation
    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalableOptionalInterface(1, typeof(IMyObject2))] // StreamJsonRpc0007 reported here
    partial interface IMyObject : IDisposable
    {
    }

    partial interface IMyObject2 : IDisposable
    {
    }
    #endregion
#pragma warning restore StreamJsonRpc0007
}

namespace StreamJsonRpc0007.Fix
{
    #region Fix
    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalableOptionalInterface(1, typeof(IMyObject2))]
    partial interface IMyObject : IDisposable
    {
    }

    [RpcMarshalable(IsOptional = true), TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    partial interface IMyObject2 : IDisposable
    {
    }
    #endregion
}
