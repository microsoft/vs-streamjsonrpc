// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CSharpSourceGeneratorVerifier<StreamJsonRpc.Analyzers.ProxyGenerator>;

public class GenerationTests
{
    [Fact]
    public async Task PublicInterface()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            public interface IMyRpc
            {
                Task MyMethodAsync(CancellationToken cancellationToken);
            }
            """);
    }
}
