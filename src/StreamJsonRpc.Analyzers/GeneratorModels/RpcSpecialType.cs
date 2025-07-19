// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1602 // Enumeration items should be documented

namespace StreamJsonRpc.Analyzers.GeneratorModels;

/// <summary>
/// The various special types that the generator must recognize.
/// </summary>
internal enum RpcSpecialType
{
    Other,
    Void,
    Task,
    ValueTask,
    IAsyncEnumerable,
    CancellationToken,
}
