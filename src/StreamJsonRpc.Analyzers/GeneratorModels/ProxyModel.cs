﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record ProxyModel : FormattableModel
{
    private readonly ImmutableEquatableArray<FormattableModel> formattableElements;

    internal ProxyModel(ImmutableEquatableSet<InterfaceModel> interfaces, string? externalProxyName = null)
    {
        if (interfaces.Count == 0)
        {
            throw new ArgumentException("Must include at least one interface.", nameof(interfaces));
        }

        this.Interfaces = interfaces;

        if (externalProxyName is null)
        {
            string name = CreateProxyName(interfaces);
            this.Name = $"{name.Replace('.', '_')}_Proxy";
            this.FileName = $"{name.Replace('<', '_').Replace('>', '_')}.g.cs";
        }
        else
        {
            this.Name = externalProxyName;
        }

        int methodSuffix = 0;
        this.formattableElements = this.Interfaces.SelectMany(i => i.Methods).Concat<FormattableModel>(
            this.Interfaces.SelectMany(i => i.Events)).Distinct()
            .Select(e => e is MethodModel method ? method with { UniqueSuffix = ++methodSuffix } : e)
            .ToImmutableEquatableArray();
    }

    internal ImmutableEquatableSet<InterfaceModel> Interfaces { get; }

    internal string Name { get; }

    internal string? FileName { get; }

    internal void WriteInterfaceMapping(SourceWriter writer, InterfaceModel iface)
    {
        writer.WriteLine($$"""
            [global::StreamJsonRpc.Reflection.JsonRpcProxyMappingAttribute(typeof({{ProxyGenerator.GenerationNamespace}}.{{this.Name}}))]
            partial interface {{iface.Name}}
            {
            }
            """);
    }

    internal void GenerateSource(SourceProductionContext context, bool isPublic)
    {
        if (this.FileName is null)
        {
            // This proxy has no source to emit. It represents a pre-existing external proxy.
            return;
        }

        // TODO: consider declaring the proxy type with equivalent visibility as the interface,
        //       since a public interface needs a publicly accessible proxy.
        //       Otherwise Reflection is required to access the type.
        SourceWriter writer = new();
        writer.WriteLine("""
            // <auto-generated/>

            #nullable enable
            #pragma warning disable CS0436 // prefer local types to imported ones

            """);

        // Add attributes to interfaces we implement that are in this compilation and are recursively partial
        // so that at runtime, the proxy can be discovered by reflection.
        foreach (InterfaceModel iface in this.Interfaces)
        {
            if (!iface.DeclaredInThisCompilation || !iface.IsFullyPartial)
            {
                continue;
            }

            if (iface.Container is null)
            {
                this.WriteInterfaceMapping(writer, iface);
            }
            else
            {
                iface.Container.WriteWithin(writer, writer => this.WriteInterfaceMapping(writer, iface));
            }

            writer.WriteLine();
        }

        writer.WriteLine($"namespace {ProxyGenerator.GenerationNamespace}");
        writer.WriteLine("{");
        writer.Indentation++;

        bool anyIfaceIsPublic = this.Interfaces.Any(i => i.IsPublic);
        string visibility = isPublic && anyIfaceIsPublic ? "public" : "internal";

        writer.WriteLine($$"""

            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("{{ThisAssembly.AssemblyName}}", "{{ThisAssembly.AssemblyFileVersion}}")]
            """);
        if (isPublic)
        {
            writer.WriteLine($$"""
            [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
            """);
        }

        writer.WriteLine($$"""
            {{visibility}} class {{this.Name}} : global::StreamJsonRpc.Reflection.ProxyBase
            """);

        writer.Indentation++;
        foreach (InterfaceModel iface in this.Interfaces)
        {
            writer.WriteLine($", global::{iface.FullName}");
        }

        writer.Indentation--;
        writer.WriteLine("""
                {
                """);

        writer.Indentation++;
        this.WriteFields(writer);

        this.WriteConstructor(writer);

        this.WriteEvents(writer);
        this.WriteProperties(writer);
        this.WriteMethods(writer);
        this.WriteNestedTypes(writer);

        writer.Indentation--;
        writer.WriteLine("}");

        writer.Indentation--;
        writer.WriteLine("}");

        context.AddSource(this.FileName, writer.ToSourceText());
    }

    internal override void WriteEvents(SourceWriter writer)
    {
        foreach (FormattableModel formattable in this.formattableElements)
        {
            formattable.WriteEvents(writer);
        }
    }

    internal override void WriteHookupStatements(SourceWriter writer)
    {
        foreach (FormattableModel formattable in this.formattableElements)
        {
            formattable.WriteHookupStatements(writer);
        }
    }

    internal override void WriteMethods(SourceWriter writer)
    {
        foreach (FormattableModel formattable in this.formattableElements)
        {
            formattable.WriteMethods(writer);
        }
    }

    internal override void WriteFields(SourceWriter writer)
    {
        foreach (FormattableModel formattable in this.formattableElements)
        {
            formattable.WriteFields(writer);
        }
    }

    internal override void WriteProperties(SourceWriter writer)
    {
        foreach (FormattableModel formattable in this.formattableElements)
        {
            formattable.WriteProperties(writer);
        }
    }

    internal override void WriteNestedTypes(SourceWriter writer)
    {
        foreach (FormattableModel formattable in this.formattableElements)
        {
            formattable.WriteNestedTypes(writer);
        }
    }

    private void WriteConstructor(SourceWriter writer)
    {
        writer.WriteLine($$"""

                public {{this.Name}}(global::StreamJsonRpc.JsonRpc client, global::StreamJsonRpc.Reflection.ProxyInputs inputs)
                    : base(client, inputs)
                {
                """);

        writer.Indentation++;
        this.WriteHookupStatements(writer);

        writer.Indentation--;
        writer.WriteLine("""
                }
                """);
    }

    private static string CreateProxyName(ImmutableEquatableSet<InterfaceModel> interfaces)
    {
        // We need to create a unique, deterministic name given the set of interfaces the proxy must implement.
        if (interfaces.Count == 1)
        {
            // If there's just one, keep it simple.
            return interfaces.Single().FullName;
        }

        // More than one, start by sorting them. Then use the full interface name of the first element, and hash the rest.
        string[] sorted = [.. interfaces.Select(i => i.FullName)];
        Array.Sort(sorted, StringComparer.Ordinal);

        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        for (int i = 1; i < sorted.Length; i++)
        {
            builder.Append(sorted[i]);
            builder.Append("\r\n");
        }

        byte[] additionalInterfaceBytes = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] additionalInterfaceHash = sha.ComputeHash(additionalInterfaceBytes);
        string additionalInterfaceHashString = Convert.ToBase64String(additionalInterfaceHash).TrimEnd('=').Replace('+', '_').Replace('/', '_');
        return $"{sorted[0]}{additionalInterfaceHashString[..8]}";
    }
}
