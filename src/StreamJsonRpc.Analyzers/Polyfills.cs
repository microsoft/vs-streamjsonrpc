// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable CS9113 // Parameter is unread.
#pragma warning disable SA1649 // File name should match first type name

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    internal sealed class CollectionBuilderAttribute(Type builderType, string methodName) : Attribute;
}
