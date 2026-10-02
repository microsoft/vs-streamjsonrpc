// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;

namespace Benchmarks;

/// <summary>
/// A source document within a <see cref="Project"/>.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public class Document
{
    /// <summary>
    /// Gets or sets the repo-relative path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of lines in the document.
    /// </summary>
    public int LineCount { get; set; }

    /// <summary>
    /// Gets or sets the last modified timestamp in ticks.
    /// </summary>
    public long LastModifiedTicks { get; set; }

    /// <summary>
    /// Gets or sets the content checksum.
    /// </summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the diagnostics reported for this document.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; set; } = new();
}
