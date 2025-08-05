// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using StreamJsonRpc.Protocol;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// The constant "jsonrpc".
    /// </summary>
    private static readonly MessagePackString VersionPropertyName = new(Constants.jsonrpc);

    /// <summary>
    /// The constant "id".
    /// </summary>
    private static readonly MessagePackString IdPropertyName = new(Constants.id);

    /// <summary>
    /// The constant "method".
    /// </summary>
    private static readonly MessagePackString MethodPropertyName = new(Constants.Request.method);

    /// <summary>
    /// The constant "result".
    /// </summary>
    private static readonly MessagePackString ResultPropertyName = new(Constants.Result.result);

    /// <summary>
    /// The constant "error".
    /// </summary>
    private static readonly MessagePackString ErrorPropertyName = new(Constants.Error.error);

    /// <summary>
    /// The constant "params".
    /// </summary>
    private static readonly MessagePackString ParamsPropertyName = new(Constants.Request.@params);

    /// <summary>
    /// The constant "traceparent".
    /// </summary>
    private static readonly MessagePackString TraceParentPropertyName = new(Constants.Request.traceparent);

    /// <summary>
    /// The constant "tracestate".
    /// </summary>
    private static readonly MessagePackString TraceStatePropertyName = new(Constants.Request.tracestate);

    /// <summary>
    /// The constant "2.0".
    /// </summary>
    private static readonly MessagePackString Version2 = new("2.0");
}
