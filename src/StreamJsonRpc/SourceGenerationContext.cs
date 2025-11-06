// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json.Serialization;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// System.Text.Json source generation context for StreamJsonRpc types.
/// </summary>
[JsonSerializable(typeof(RequestId))]
[JsonSerializable(typeof(MessageFormatterRpcMarshaledContextTracker.MarshalToken))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(CommonErrorData))]
internal partial class SourceGenerationContext : JsonSerializerContext;
