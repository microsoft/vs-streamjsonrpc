// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Uncomment the following line to write expected files to disk
////#define WRITE_EXPECTED

#if WRITE_EXPECTED
#warning WRITE_EXPECTED is fine for local builds, but should not be merged to the main branch.
#endif

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using StreamJsonRpc.Analyzers;

internal static partial class CSharpSourceGeneratorVerifier<TSourceGenerator>
    where TSourceGenerator : new()
{
    internal const string SourceFilePrefix = /* lang=c#-test */ """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using StreamJsonRpc;

        """;

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
                        {SourceFilePrefix}
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

        public GeneratorConfiguration GeneratorConfiguration { get; set; } = GeneratorConfiguration.Default;

        public LanguageVersion LanguageVersion { get; set; } = DefaultLanguageVersion;

        public async Task RunDefaultAsync([StringSyntax("c#-test")] string testSource, LanguageVersion languageVersion = DefaultLanguageVersion, [CallerFilePath] string? testFile = null, [CallerMemberName] string testMethod = null!)
        {
            await this.RunAsync();
        }

        public Test AddGeneratedSources()
        {
            static void AddGeneratedSources(ProjectState project, string testMethod, bool withPrefix)
            {
                string prefix = withPrefix ? $"{project.Name}." : string.Empty;
                string expectedPrefix = $"{typeof(Test).Assembly.GetName().Name}.Resources.{testMethod}.{prefix}"
                    .Replace(' ', '_')
                    .Replace(',', '_')
                    .Replace('(', '_')
                    .Replace(')', '_');

                foreach (var resourceName in typeof(Test).Assembly.GetManifestResourceNames())
                {
                    if (!resourceName.StartsWith(expectedPrefix))
                    {
                        continue;
                    }

                    using Stream? resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (resourceStream is null)
                    {
                        throw new InvalidOperationException();
                    }

                    using var reader = new StreamReader(resourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                    var name = resourceName.Substring(expectedPrefix.Length);
                    var code = reader.ReadToEnd();

                    // Update the hard-coded version to the one that would be generated if done with this version.
                    // This is so the just-generated code matches what we load from disk.
                    code = Regex.Replace(code, @"GeneratedCodeAttribute\(""([^""]+)"", ""[^""]+""\)", m => $@"GeneratedCodeAttribute(""{m.Groups[1].Value}"", ""{AnalyzerAssembly.AssemblyFileVersion}"")");

                    project.GeneratedSources.Add((typeof(TSourceGenerator), name, code));
                }
            }

            AddGeneratedSources(this.TestState, this.testMethod, this.TestState.AdditionalProjects.Count > 0);
            foreach (ProjectState addlProject in this.TestState.AdditionalProjects.Values)
            {
                AddGeneratedSources(addlProject, this.testMethod, true);
            }

            return this;
        }

        protected override Task RunImplAsync(CancellationToken cancellationToken)
        {
            this.AddGeneratedSources();

            foreach (ProjectState addlProject in this.TestState.AdditionalProjects.Values)
            {
                addlProject.AdditionalReferences.AddRange(this.TestState.AdditionalReferences);
                addlProject.DocumentationMode = DocumentationMode.Parse;
            }

            this.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", this.GeneratorConfiguration.ToGlobalConfigString()));

            return base.RunImplAsync(cancellationToken);
        }

        protected override ParseOptions CreateParseOptions()
        {
            var parseOptions = (CSharpParseOptions)base.CreateParseOptions();
            return parseOptions
                .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", ProxyGenerator.GenerationNamespace)]);
        }

        protected override CompilationOptions CreateCompilationOptions()
        {
            var compilationOptions = (CSharpCompilationOptions)base.CreateCompilationOptions();
            return compilationOptions
                .WithAllowUnsafe(false)
                .WithWarningLevel(99)
                .WithSpecificDiagnosticOptions(compilationOptions.SpecificDiagnosticOptions
                    .SetItem("CS1591", ReportDiagnostic.Suppress)
                    .SetItem("CS1701", ReportDiagnostic.Suppress)
                    .SetItem("CS1702", ReportDiagnostic.Suppress));
        }

        protected override async Task<(Compilation, ImmutableArray<Diagnostic>)> GetProjectCompilationAsync(Project project, IVerifier verifier, CancellationToken cancellationToken)
        {
            string fileNamePrefix = this.TestState.AdditionalProjects.Count > 0 ? $"{project.Name}." : string.Empty;
            var resourceDirectory = Path.Combine(Path.GetDirectoryName(this.testFile)!, "Resources", this.testMethod);

            (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = await base.GetProjectCompilationAsync(project, verifier, cancellationToken);

            // Log all source, but put the source with diagnostics first.
            SyntaxTree?[] documentsWithDiagnostics = [.. compilation.GetDiagnostics(cancellationToken).Select(d => d.Location.SourceTree).Distinct()];
            foreach (SyntaxTree? tree in documentsWithDiagnostics.Concat(compilation.SyntaxTrees.Except(documentsWithDiagnostics)))
            {
                NumberedLineWriter.LogSyntaxTree(tree, cancellationToken);
            }

            var expectedNames = new HashSet<string>();
            foreach (SyntaxTree? tree in compilation.SyntaxTrees.Skip(project.DocumentIds.Count))
            {
                WriteTreeToDiskIfNecessary(tree, resourceDirectory, fileNamePrefix);
                expectedNames.Add(fileNamePrefix + Path.GetFileName(tree.FilePath));
            }

            PurgeExtranneousFiles(resourceDirectory, fileNamePrefix, expectedNames);

#if !WRITE_EXPECTED
            var currentTestPrefix = $"{typeof(Test).Assembly.GetName().Name}.Resources.{this.testMethod}.{fileNamePrefix}";
            foreach (var name in this.GetType().Assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(currentTestPrefix))
                {
                    continue;
                }

                if (!expectedNames.Contains(name.Substring(currentTestPrefix.Length - fileNamePrefix.Length)))
                {
                    throw new InvalidOperationException($"Unexpected test resource: {name.Substring(currentTestPrefix.Length)}");
                }
            }
#endif

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

            string code = tree.GetText().ToString();

            // Remove the version number from the file written to disk to keep the changed files noise down.
            code = Regex.Replace(code, @"GeneratedCodeAttribute\(""([^""]+)"", ""[^""]+""\)", m => $@"GeneratedCodeAttribute(""{m.Groups[1].Value}"", ""x.x.x.x"")");

            File.WriteAllText(filePath, code, tree.Encoding);
        }

        [Conditional("WRITE_EXPECTED")]
        private static void PurgeExtranneousFiles(string resourceDirectory, string fileNamePrefix, HashSet<string> expectedNames)
        {
            if (!Directory.Exists(resourceDirectory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(resourceDirectory, $"{fileNamePrefix}*"))
            {
                if (!expectedNames.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
