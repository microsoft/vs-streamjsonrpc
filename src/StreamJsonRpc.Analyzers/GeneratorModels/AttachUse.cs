// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis.CSharp;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record AttachUse(InterceptableLocation InterceptableLocation, AttachSignature Signature, ImmutableEquatableSet<InterfaceModel> Contracts);
