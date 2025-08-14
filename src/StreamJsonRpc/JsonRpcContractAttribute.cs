// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Identifies an interface that describes an RPC contract.
/// </summary>
/// <remarks>
/// This attribute may trigger source generation of a proxy class that implements the interface.
/// It may also trigger analyzers that verify the interfaces conforms to RPC contract rules.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface)]
public class JsonRpcContractAttribute : Attribute;
