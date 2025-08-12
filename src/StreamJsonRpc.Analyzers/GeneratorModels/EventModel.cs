// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record EventModel(string Name, string DelegateType, string EventArgsType) : FormattableModel
{
    internal override void WriteHookupStatements(SourceWriter writer)
    {
        writer.WriteLine($"""
                this.JsonRpc.AddLocalRpcMethod(this.Options.EventNameTransform("{this.Name}"), this.On{this.Name});
                """);
    }

    internal override void WriteEvents(SourceWriter writer)
    {
        writer.WriteLine($$"""

                public event {{this.DelegateType}}? {{this.Name}};

                protected virtual void On{{this.Name}}({{this.EventArgsType}} args) => this.{{this.Name}}?.Invoke(this, args);
                """);
    }

    internal static EventModel? Create(IEventSymbol evt, KnownSymbols symbols)
    {
        if (evt.Type is not INamedTypeSymbol { DelegateInvokeMethod: { } invokeMethod })
        {
            return null;
        }

        return new EventModel(evt.Name, evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), invokeMethod.Parameters[1].Type.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat));
    }
}
