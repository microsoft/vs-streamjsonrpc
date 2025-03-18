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
                Task JustCancellationAsync(CancellationToken cancellationToken);
                ValueTask AnArgAndCancellationAsync(int arg, CancellationToken cancellationToken);
                Task<int> AddAsync(int a, int b, CancellationToken cancellationToken);
                Task<int> MultiplyAsync(int a, int b);
                void Start(string bah);
                void StartCancelable(string bah, CancellationToken token);
                IAsyncEnumerable<int> CountAsync(int start, int count, CancellationToken cancellationToken);
            }
            """);
    }
}
