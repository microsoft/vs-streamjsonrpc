﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Uncomment the following line to write expected files to disk
#define WRITE_EXPECTED

#if WRITE_EXPECTED
#warning WRITE_EXPECTED is fine for local builds, but should not be merged to the main branch.
#endif

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

internal static partial class CSharpSourceGeneratorVerifier<TSourceGenerator>
    where TSourceGenerator : new()
{
    private const LanguageVersion DefaultLanguageVersion = LanguageVersion.CSharp7_3;

    public static Task RunDefaultAsync([StringSyntax("c#-test")] string testSource, LanguageVersion languageVersion = DefaultLanguageVersion, [CallerFilePath] string testFile = null!, [CallerMemberName] string testMethod = null!)
    {
        Test test = new(testFile: testFile, testMethod: testMethod)
        {
            TestState =
                {
                    Sources =
                    {
                        $"""
                        using System.Threading;
                        using System.Threading.Tasks;
                        using StreamJsonRpc;

                        {testSource}
                        """,
                    },
                },
            LanguageVersion = languageVersion,
        };

        return test.RunDefaultAsync(testSource);
    }

    internal class Test : CSharpSourceGeneratorTest<TSourceGenerator, DefaultVerifier>
    {
        private readonly string? testFile;
        private readonly string testMethod;

        public Test([CallerFilePath] string testFile = null!, [CallerMemberName] string testMethod = null!)
        {
            this.CompilerDiagnostics = CompilerDiagnostics.Warnings;
            this.ReferenceAssemblies = ReferencesHelper.References;
            this.TestState.AdditionalReferences.AddRange(ReferencesHelper.GetReferences());

            this.testFile = testFile;
            this.testMethod = testMethod;

#if WRITE_EXPECTED
            this.TestBehaviors |= TestBehaviors.SkipGeneratedSourcesCheck;
#endif
        }

        public LanguageVersion LanguageVersion { get; set; } = DefaultLanguageVersion;

        public async Task RunDefaultAsync([StringSyntax("c#-test")] string testSource, LanguageVersion languageVersion = DefaultLanguageVersion, [CallerFilePath] string? testFile = null, [CallerMemberName] string testMethod = null!)
        {
            ITestOutputHelper logger = TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException();
            try
            {
                await this.RunAsync();
            }
            finally
            {
                foreach ((string fileName, SourceText content) in this.TestState.GeneratedSources)
                {
                    logger.WriteLine("--------------------------------------------------------------");
                    logger.WriteLine(fileName);
                    logger.WriteLine("--------------------------------------------------------------");
                    int lineNumber = 0;
                    foreach (TextLine line in content.Lines)
                    {
                        logger.WriteLine($"{++lineNumber,6}: {content.GetSubText(line.Span)}");
                    }

                    logger.WriteLine("--------------------------------------------------------------");
                }
            }
        }

        protected override CompilationOptions CreateCompilationOptions()
        {
            var compilationOptions = (CSharpCompilationOptions)base.CreateCompilationOptions();
            return compilationOptions
                .WithAllowUnsafe(false)
                .WithWarningLevel(99)
                .WithSpecificDiagnosticOptions(compilationOptions.SpecificDiagnosticOptions
                    .SetItem("CS1591", ReportDiagnostic.Suppress)
                    .SetItem("CS1701", ReportDiagnostic.Suppress));
        }

        protected override async Task<(Compilation, ImmutableArray<Diagnostic>)> GetProjectCompilationAsync(Project project, IVerifier verifier, CancellationToken cancellationToken)
        {
            string fileNamePrefix = this.TestState.AdditionalProjects.Count > 0 ? $"{project.Name}." : string.Empty;
            var resourceDirectory = Path.Combine(Path.GetDirectoryName(this.testFile)!, "Resources", this.testMethod);

            (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = await base.GetProjectCompilationAsync(project, verifier, cancellationToken);
            var expectedNames = new HashSet<string>();
            foreach (SyntaxTree? tree in compilation.SyntaxTrees.Skip(project.DocumentIds.Count))
            {
                WriteTreeToDiskIfNecessary(tree, resourceDirectory, fileNamePrefix);
                expectedNames.Add(Path.GetFileName(tree.FilePath));
            }

            var currentTestPrefix = $"{typeof(Test).Assembly.GetName().Name}.Resources.{this.testMethod}.{fileNamePrefix}";
            foreach (var name in this.GetType().Assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(currentTestPrefix))
                {
                    continue;
                }

                if (!expectedNames.Contains(name.Substring(currentTestPrefix.Length)))
                {
                    throw new InvalidOperationException($"Unexpected test resource: {name.Substring(currentTestPrefix.Length)}");
                }
            }

            return (compilation, diagnostics);
        }

        [Conditional("WRITE_EXPECTED")]
        private static void WriteTreeToDiskIfNecessary(SyntaxTree tree, string resourceDirectory, string fileNamePrefix)
        {
            if (tree.Encoding is null)
            {
                throw new ArgumentException("Syntax tree encoding was not specified");
            }

            string name = fileNamePrefix + Path.GetFileName(tree.FilePath);
            string filePath = Path.Combine(resourceDirectory, name);
            Directory.CreateDirectory(resourceDirectory);
            File.WriteAllText(filePath, tree.GetText().ToString(), tree.Encoding);
        }
    }
}
