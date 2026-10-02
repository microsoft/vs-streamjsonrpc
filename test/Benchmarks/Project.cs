// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;

namespace Benchmarks;

/// <summary>
/// A project within a <see cref="Workspace"/>.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public class Project
{
    /// <summary>
    /// Gets or sets the project name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target framework moniker.
    /// </summary>
    public string TargetFramework { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the project produces an executable.
    /// </summary>
    public bool IsExecutable { get; set; }

    /// <summary>
    /// Gets or sets the referenced assembly names.
    /// </summary>
    public List<string> References { get; set; } = new();

    /// <summary>
    /// Gets or sets the documents in the project.
    /// </summary>
    public List<Document> Documents { get; set; } = new();
}
