// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Samples.Analyzers.NoProxy;

/// <summary>
/// An attribute that serves to allow sample code to compile and look valid for documentation,
/// but does not actually trigger proxy generation.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
internal class JsonRpcContractAttribute : Attribute;
