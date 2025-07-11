// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using StreamJsonRpc.Analyzers;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterfaceModel(string Prefix, string InterfaceName, ImmutableEquatableArray<MethodModel> Methods, ImmutableEquatableArray<EventModel?> Events) : FormattableModel
{
    internal IEnumerable<FormattableModel> FormattableElements => this.Methods.Concat<FormattableModel>(this.Events.Where(e => e is not null)!);

    internal static InterfaceModel Create(INamedTypeSymbol iface, KnownSymbols symbols)
    {
        int methodIndex = 0;
        ImmutableEquatableArray<MethodModel> methods = new([..
            iface.GetAllMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.AssociatedSymbol is null && !SymbolEqualityComparer.Default.Equals(m.ContainingType, symbols.IDisposable))
                .Select(method => MethodModel.Create(method, symbols, ++methodIndex))]);

        ImmutableEquatableArray<EventModel?> events = new([..
            iface.GetAllMembers()
                .OfType<IEventSymbol>()
                .Select(evt => EventModel.Create(evt, symbols))]);

        string fileNamePrefix = iface.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat);
        return new InterfaceModel(
            fileNamePrefix,
            iface.ToDisplayString(ProxyGenerator.FullyQualifiedNoGlobalWithNullableFormat),
            methods,
            events);
    }

    internal override void WriteEvents(SourceWriter writer, InterfaceModel ifaceModel)
    {
        foreach (FormattableModel formattable in this.FormattableElements)
        {
            formattable.WriteEvents(writer, ifaceModel);
        }
    }

    internal override void WriteHookupStatements(SourceWriter writer, InterfaceModel ifaceModel)
    {
        foreach (FormattableModel formattable in this.FormattableElements)
        {
            formattable.WriteHookupStatements(writer, ifaceModel);
        }
    }

    internal override void WriteMethods(SourceWriter writer, InterfaceModel ifaceModel)
    {
        foreach (FormattableModel formattable in this.FormattableElements)
        {
            formattable.WriteMethods(writer, ifaceModel);
        }
    }

    internal override void WriteFields(SourceWriter writer, InterfaceModel ifaceModel)
    {
        foreach (FormattableModel formattable in this.FormattableElements)
        {
            formattable.WriteFields(writer, ifaceModel);
        }
    }

    internal override void WriteProperties(SourceWriter writer, InterfaceModel ifaceModel)
    {
        foreach (FormattableModel formattable in this.FormattableElements)
        {
            formattable.WriteProperties(writer, ifaceModel);
        }
    }

    internal override void WriteNestedTypes(SourceWriter writer, InterfaceModel ifaceModel)
    {
        foreach (FormattableModel formattable in this.FormattableElements)
        {
            formattable.WriteNestedTypes(writer, ifaceModel);
        }
    }
}
