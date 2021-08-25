// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using StreamJsonRpc.Protocol;

    public interface IJsonRpcMessageFactory
    {
        JsonRpcRequest CreateRequestMessage();

        JsonRpcError CreateErrorMessage();

        JsonRpcResult CreateResultMessage();
    }
}
