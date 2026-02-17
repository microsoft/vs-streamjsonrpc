// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;
using PolyType;

internal static class ReferencesHelper
{
    internal static readonly string NuGetConfigPath = FindNuGetConfigPath();

#if NET
    internal static ReferenceAssemblies References = ReferenceAssemblies.Net.Net80
        .WithNuGetConfigFilePath(NuGetConfigPath);
#else
    internal static ReferenceAssemblies References = ReferenceAssemblies.NetStandard.NetStandard20
        .WithNuGetConfigFilePath(NuGetConfigPath)
        .AddPackages([
            new PackageIdentity("System.Memory", "4.6.3"),
            new PackageIdentity("System.Threading.Tasks.Extensions", "4.6.1"),
            new PackageIdentity("Microsoft.Bcl.AsyncInterfaces", "10.0.1"),
            ]);
#endif

    internal static IEnumerable<MetadataReference> GetReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(JsonRpc).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(GenerateShapeAttribute).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(IDisposableObservable).Assembly.Location);
    }

    private static string FindNuGetConfigPath()
    {
        string? path = AppContext.BaseDirectory;
        while (path is not null)
        {
            string candidate = Path.Combine(path, "nuget.config");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            path = Path.GetDirectoryName(path);
        }

        throw new InvalidOperationException("Could not find NuGet.config by searching up from " + AppContext.BaseDirectory);
    }
}
