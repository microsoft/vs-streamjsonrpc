// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal abstract record FormattableModel
{
    internal virtual void WriteFields(SourceWriter writer)
    {
    }

    internal virtual void WriteProperties(SourceWriter writer)
    {
    }

    internal virtual void WriteHookupStatements(SourceWriter writer)
    {
    }

    internal virtual void WriteEvents(SourceWriter writer)
    {
    }

    internal virtual void WriteMethods(SourceWriter writer)
    {
    }

    internal virtual void WriteNestedTypes(SourceWriter writer)
    {
    }
}
