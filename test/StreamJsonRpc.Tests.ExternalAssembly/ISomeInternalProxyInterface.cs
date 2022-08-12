// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;

namespace StreamJsonRpc.Tests.ExternalAssembly
{
    internal interface ISomeInternalProxyInterface
    {
        Task<int> SubtractAsync(int a, int b);
    }
}
