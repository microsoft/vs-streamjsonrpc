// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MessagePack;

namespace Benchmarks;

/// <summary>
/// A diagnostic reported against a <see cref="Document"/>.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public class Diagnostic
{
    /// <summary>
    /// Gets or sets the diagnostic identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the severity.
    /// </summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the line number.
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// Gets or sets the column number.
    /// </summary>
    public int Column { get; set; }

    /// <summary>
    /// Gets or sets the human-readable message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
