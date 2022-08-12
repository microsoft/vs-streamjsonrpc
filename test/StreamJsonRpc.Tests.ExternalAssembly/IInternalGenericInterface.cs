// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Tests.ExternalAssembly
{
    internal interface IInternalGenericInterface<TOptions>
    {
        Task<TOptions> GetOptionsAsync(InternalStruct id, CancellationToken cancellationToken);
    }
}
