// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Access to the <see cref="ThisAssembly"/> in the analyzer project.
/// </summary>
public static class AnalyzerAssembly
{
    /// <summary>
    /// Gets the file version of the assembly.
    /// </summary>
    public static string AssemblyFileVersion => ThisAssembly.AssemblyFileVersion;
}
