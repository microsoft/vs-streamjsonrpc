// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Offers code fixes for diagnostics reported by <see cref="JsonRpcContractAnalyzer"/>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp)]
public class JsonRpcContractCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// Indicates whether an artificial line normalization happens for tests' sake.
    /// </summary>
#pragma warning disable SA1401 // Fields should be private
    public static bool NormalizeLineEndings;
#pragma warning restore SA1401 // Fields should be private

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds => [
        JsonRpcContractAnalyzer.RpcMarshableDisposableId,
        JsonRpcContractAnalyzer.GeneratePolyTypeMethodsOnRpcContractInterfaceId,
    ];

    /// <inheritdoc/>
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            switch (diagnostic.Id)
            {
                case JsonRpcContractAnalyzer.RpcMarshableDisposableId:
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: Strings.RpcMarshableDisposable_FixTitle,
                            createChangedDocument: AddIDisposableBaseTypeAsync,
                            equivalenceKey: nameof(AddIDisposableBaseTypeAsync)),
                        diagnostic);
                    break;
                case JsonRpcContractAnalyzer.GeneratePolyTypeMethodsOnRpcContractInterfaceId when diagnostic.AdditionalLocations is [{ } only] && only.Equals(Location.None):
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: Strings.GeneratePolyTypeMethodsOnRpcContractInterface_FixTitle,
                            createChangedDocument: AddMethodShapesAsync,
                            equivalenceKey: nameof(AddMethodShapesAsync)),
                        diagnostic);
                    break;
            }

            async Task<(SyntaxNode Root, BaseTypeDeclarationSyntax TypeDeclaration)?> FindSyntax(CancellationToken cancellation)
            {
                SyntaxNode? root = await context.Document.GetSyntaxRootAsync(cancellation);
                if (root is null)
                {
                    return null;
                }

                BaseTypeDeclarationSyntax? typeDecl = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
                if (typeDecl is null)
                {
                    return null;
                }

                return (root, typeDecl);
            }

            async Task<Document> FinalizeDocument((SyntaxNode Root, BaseTypeDeclarationSyntax TypeDeclaration) nodes, BaseTypeDeclarationSyntax modifiedTypeDecl, CancellationToken cancellation)
            {
                Document modifiedDocument = await AddImportAndSimplifyAsync(context.Document.WithSyntaxRoot(nodes.Root.ReplaceNode(nodes.TypeDeclaration, modifiedTypeDecl)), cancellation);
                return modifiedDocument;
            }

            async Task<Document> AddIDisposableBaseTypeAsync(CancellationToken cancellation)
            {
                if (await FindSyntax(cancellation) is not { } nodes)
                {
                    return context.Document;
                }

                BaseTypeDeclarationSyntax modifiedTypeDecl = nodes.TypeDeclaration.AddBaseListTypes(SimpleBaseType(
                    ParseTypeName("global::System.IDisposable").WithAdditionalAnnotations(Simplifier.AddImportsAnnotation)));

                // Move the new line for better formatting, if necessary.
                if (nodes.TypeDeclaration.BaseList is null && nodes.TypeDeclaration.Identifier.HasTrailingTrivia)
                {
                    modifiedTypeDecl = modifiedTypeDecl.WithIdentifier(modifiedTypeDecl.Identifier.WithTrailingTrivia(SyntaxTriviaList.Empty));
                }

                return await FinalizeDocument(nodes, modifiedTypeDecl, cancellation);
            }

            async Task<Document> AddMethodShapesAsync(CancellationToken cancellation)
            {
                if (await FindSyntax(cancellation) is not { } nodes)
                {
                    return context.Document;
                }

                // Add a whole new TypeShapeAttribute.
                bool preferGenerateShape = diagnostic.Properties.TryGetValue("PreferGenerateShape", out string? value) && value == "true";
                BaseTypeDeclarationSyntax modifiedTypeDecl = nodes.TypeDeclaration
                    .AddAttributeLists(AttributeList(SingletonSeparatedList(Attribute(ParseName(preferGenerateShape ? "PolyType.GenerateShape" : "PolyType.TypeShape")).AddArgumentListArguments(
                        AttributeArgument(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("MethodShapeFlags"), IdentifierName("PublicInstance"))).WithNameEquals(NameEquals(IdentifierName("IncludeMethods"))))))
                    .WithAdditionalAnnotations(Simplifier.AddImportsAnnotation, Formatter.Annotation));

                return await FinalizeDocument(nodes, modifiedTypeDecl, cancellation);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task<Document> AddImportAndSimplifyAsync(Document document, CancellationToken cancellationToken)
    {
        Document modifiedDocument = document;
        modifiedDocument = await ImportAdder.AddImportsAsync(modifiedDocument, Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken);
        modifiedDocument = await Simplifier.ReduceAsync(modifiedDocument, cancellationToken: cancellationToken);
        modifiedDocument = await Formatter.FormatAsync(modifiedDocument, Formatter.Annotation, cancellationToken: cancellationToken);

        // If in tests, normalize to CRLF line endings so codefix tests can pass.
        if (NormalizeLineEndings)
        {
            SourceText text = await modifiedDocument.GetTextAsync(cancellationToken);
            modifiedDocument = modifiedDocument.WithText(SourceText.From(text.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n")));
        }

        return modifiedDocument;
    }
}
