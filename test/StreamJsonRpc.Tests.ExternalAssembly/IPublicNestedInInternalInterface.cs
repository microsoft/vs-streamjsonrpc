// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1649 // File name should match first type name

namespace StreamJsonRpc.Tests.ExternalAssembly;

internal partial interface IInternal
{
    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IPublicNestedInInternalInterface
    {
        Task<int> SubtractAsync(int a, int b);
    }
}
