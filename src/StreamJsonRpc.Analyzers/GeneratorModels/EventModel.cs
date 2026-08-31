// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record EventModel(string DeclaringType, string Name, string DelegateType, string EventArgsType, string RpcName) : FormattableModel
{
    internal override void WriteHookupStatements(SourceWriter writer)
    {
        writer.WriteLine($"""
                this.JsonRpc.AddLocalRpcMethod(this.TransformEventName("{this.RpcName}", typeof({this.DeclaringType})), this.On{this.Name});
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

        AttributeData? jsonRpcEventAttribute = evt.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.JsonRpcEventAttribute));
        string rpcName = jsonRpcEventAttribute is { ConstructorArguments: [{ Value: string jsonRpcEventName }, ..] } ? jsonRpcEventName : evt.Name;
        return new EventModel(evt.ContainingType.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat), evt.Name, evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), invokeMethod.Parameters[1].Type.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat), rpcName);
    }
}
