// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

/// <summary>
/// A namespace or nesting type that must be reproduced in generated code in order to declare a potentially nested partial type.
/// </summary>
/// <param name="Name">The leaf name of the namespace or the name of the nesting type.</param>
/// <param name="Parent">The parent namespace or nesting type.</param>
internal abstract record Container(string Name, Container? Parent = null)
{
    internal abstract bool IsPartial { get; }

    internal bool IsFullyPartial => this.IsPartial && (this.Parent is null || this.Parent.IsFullyPartial);

    internal abstract string Keywords { get; }

    internal string FullName => this.Parent is null ? this.Name : $"{this.Parent.FullName}.{this.Name}";

    /// <summary>
    /// Gets the first namespace in the chain of containers, starting with this one.
    /// </summary>
    internal Namespace? ThisOrContainingNamespace => this is Namespace ns ? ns : this.Parent?.ThisOrContainingNamespace;

    internal static Container? CreateFor(INamespaceOrTypeSymbol? symbol, CancellationToken cancellationToken)
    {
        if (symbol is null or INamespaceSymbol { IsGlobalNamespace: true })
        {
            return null;
        }

        Container? parent = CreateFor((INamespaceOrTypeSymbol)symbol.ContainingType ?? symbol.ContainingNamespace, cancellationToken);

        return symbol switch
        {
            ITypeSymbol type => new Type(type.Name, IsPartial(), GetTypeKeyword(type), parent),
            INamespaceSymbol ns => new Namespace(ns.Name, parent),
            _ => throw new ArgumentException($"Unsupported symbol type: {symbol.GetType().Name}.", nameof(symbol)),
        };

        bool IsPartial() => (symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as MemberDeclarationSyntax)?.Modifiers.Any(SyntaxKind.PartialKeyword) is true;

        static string GetTypeKeyword(ITypeSymbol symbol)
        {
            return symbol switch
            {
                { TypeKind: TypeKind.Class, IsRecord: false } => "class",
                { TypeKind: TypeKind.Class, IsRecord: true } => "record",
                { TypeKind: TypeKind.Struct, IsRecord: false } => "struct",
                { TypeKind: TypeKind.Struct, IsRecord: true } => "record struct",
                { TypeKind: TypeKind.Interface } => "interface",
                _ => throw new ArgumentException($"Unsupported type kind: {symbol.TypeKind}.", nameof(symbol)),
            };
        }
    }

    internal void WriteWithin(SourceWriter writer, Action<SourceWriter> writeContent)
    {
        this.VerifyPartiality();
        if (this.Parent is null)
        {
            this.WriteWithinCore(writer, writeContent);
        }
        else
        {
            this.Parent.WriteWithin(writer, writer => this.WriteWithinCore(writer, writeContent));
        }
    }

    private void VerifyPartiality()
    {
        if (!this.IsPartial)
        {
            throw new InvalidOperationException($"The {this.FullName} container must be declared as partial.");
        }

        this.Parent?.VerifyPartiality();
    }

    private void WriteWithinCore(SourceWriter writer, Action<SourceWriter> writeContent)
    {
        writer.WriteLine($"{this.Keywords} {this.Name}");
        writer.WriteLine("{");
        writer.Indentation++;
        writeContent(writer);
        writer.Indentation--;
        writer.WriteLine("}");
    }

    internal record Namespace(string Name, Container? Parent = null) : Container(Name, Parent)
    {
        internal override bool IsPartial => true;

        internal override string Keywords => "namespace";
    }

    internal record Type : Container
    {
        private readonly bool isPartial;
        private readonly string typeKeyword;

        internal Type(string name, bool isPartial, string typeKeyword, Container? parent = null)
            : base(name, parent)
        {
            this.isPartial = isPartial;
            this.typeKeyword = typeKeyword;
        }

        internal override bool IsPartial => this.isPartial;

        internal override string Keywords => this.IsPartial ? $"partial {this.typeKeyword}" : this.typeKeyword;
    }
}
