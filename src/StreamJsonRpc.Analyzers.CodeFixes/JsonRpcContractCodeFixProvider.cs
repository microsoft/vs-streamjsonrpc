// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
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
    /// Indicates whether an artificual line normalization happens for tests' sake.
    /// </summary>
#pragma warning disable SA1401 // Fields should be private
    public static bool NormalizeLineEndings;
#pragma warning restore SA1401 // Fields should be private

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds => [
        JsonRpcContractAnalyzer.RpcMarshableDisposableId,
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
            }

            async Task<Document> AddIDisposableBaseTypeAsync(CancellationToken cancellation)
            {
                Document document = context.Document;
                SyntaxNode? root = await document.GetSyntaxRootAsync(cancellation);
                if (root is null)
                {
                    return document;
                }

                BaseTypeDeclarationSyntax? typeDecl = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
                if (typeDecl is null)
                {
                    return document;
                }

                BaseTypeDeclarationSyntax modifiedTypeDecl = typeDecl.AddBaseListTypes(SimpleBaseType(
                    ParseTypeName("global::System.IDisposable").WithAdditionalAnnotations(Simplifier.AddImportsAnnotation)));

                // Move the new line for better formatting, if necessary.
                if (typeDecl.BaseList is null && typeDecl.Identifier.HasTrailingTrivia)
                {
                    modifiedTypeDecl = modifiedTypeDecl.WithIdentifier(modifiedTypeDecl.Identifier.WithTrailingTrivia(SyntaxTriviaList.Empty));
                }

                Document modifiedDocument = await AddImportAndSimplifyAsync(document.WithSyntaxRoot(root.ReplaceNode(typeDecl, modifiedTypeDecl)), cancellation);

                return modifiedDocument;
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
