// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;

namespace Benchmarks;

/// <summary>
/// A moderately large, deterministic object graph used to measure serializer throughput on realistic payloads.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public class Workspace
{
    /// <summary>
    /// Gets or sets the workspace name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the projects in the workspace.
    /// </summary>
    public List<Project> Projects { get; set; } = new();

    /// <summary>
    /// Creates a deterministic workspace graph.
    /// </summary>
    /// <param name="seed">The random seed.</param>
    /// <param name="projectCount">The number of projects to create.</param>
    /// <param name="filesPerProject">The number of documents per project.</param>
    /// <returns>The new graph.</returns>
    public static Workspace Create(int seed, int projectCount, int filesPerProject)
    {
        Random random = new(seed);
        Workspace workspace = new() { Name = "Contoso.Workspace" };
        for (int i = 0; i < projectCount; i++)
        {
            Project project = new()
            {
                Name = $"Contoso.Project{i}",
                TargetFramework = (i % 3) switch { 0 => "net8.0", 1 => "netstandard2.0", _ => "net472" },
                IsExecutable = i % 4 == 0,
                References = Enumerable.Range(0, 5).Select(r => $"Contoso.Dependency{(i + r) % projectCount}").ToList(),
            };

            for (int f = 0; f < filesPerProject; f++)
            {
                project.Documents.Add(new Document
                {
                    Path = $"src/Contoso.Project{i}/Folder{f % 4}/File{f}.cs",
                    LineCount = random.Next(20, 2000),
                    LastModifiedTicks = random.Next(),
                    Checksum = $"{random.Next():x8}{random.Next():x8}{random.Next():x8}{random.Next():x8}",
                    Diagnostics = Enumerable.Range(0, random.Next(0, 4)).Select(d => new Diagnostic
                    {
                        Id = $"CA{1000 + ((d * 7) % 900)}",
                        Severity = (d % 3) switch { 0 => "Error", 1 => "Warning", _ => "Info" },
                        Line = random.Next(1, 500),
                        Column = random.Next(1, 120),
                        Message = "The symbol is declared but never used in this compilation unit.",
                    }).ToList(),
                });
            }

            workspace.Projects.Add(project);
        }

        return workspace;
    }
}
