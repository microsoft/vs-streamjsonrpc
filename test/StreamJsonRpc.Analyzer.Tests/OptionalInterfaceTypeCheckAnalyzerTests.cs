// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.OptionalInterfaceTypeCheckAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class OptionalInterfaceTypeCheckAnalyzerTests
{
    [Fact]
    public async Task TraditionalTypeCheckOperatorsShouldBeReplaced()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable]
            [RpcMarshalableOptionalInterface(1, typeof(IMyObject2))]
            partial interface IMyObject : IDisposable
            {
            }

            [RpcMarshalable]
            partial interface IMyObject2 : IDisposable
            {
            }

            class OneWay
            {
                bool IsOperator(IMyObject o) => {|StreamJsonRpc0050:o is IMyObject2|};
                IMyObject2 AsOperator(IMyObject o) => {|StreamJsonRpc0050:o as IMyObject2|};
                IMyObject2 CastOperator(IMyObject o) => {|StreamJsonRpc0050:(IMyObject2)o|};
            }

            class OtherWay
            {
                bool IsOperator(IMyObject2 o) => {|StreamJsonRpc0050:o is IMyObject|};
                IMyObject AsOperator(IMyObject2 o) => {|StreamJsonRpc0050:o as IMyObject|};
                IMyObject CastOperator(IMyObject2 o) => {|StreamJsonRpc0050:(IMyObject)o|};
            }
            """);
    }

    [Fact]
    public async Task CastsToOtherInterfacesNotChecked()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable]
            [RpcMarshalableOptionalInterface(1, typeof(IMyObject2))]
            partial interface IMyObject : IDisposable
            {
            }

            [RpcMarshalable]
            partial interface IMyObject2 : IDisposable
            {
            }

            class OneWay
            {
                bool IsOperator(IMyObject o) => o is IDisposable;
                IDisposable AsOperator(IMyObject o) => o as IDisposable;
                IDisposable CastOperator(IMyObject o) => (IDisposable)o;
            }

            class OtherWay
            {
                bool IsOperator(IDisposable o) => o is IMyObject;
                IMyObject AsOperator(IDisposable o) => o as IMyObject;
                IMyObject CastOperator(IDisposable o) => (IMyObject)o;
            }
            """);
    }
}
