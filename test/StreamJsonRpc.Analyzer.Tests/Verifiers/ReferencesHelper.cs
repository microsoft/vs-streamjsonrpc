// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;
#if POLYTYPE
using PolyType;
#endif

internal static class ReferencesHelper
{
#if NET
    internal static ReferenceAssemblies References = ReferenceAssemblies.Net.Net80;
#else
    internal static ReferenceAssemblies References = ReferenceAssemblies.NetStandard.NetStandard20
        .AddPackages([
            new PackageIdentity("System.Memory", "4.6.3"),
            new PackageIdentity("System.Threading.Tasks.Extensions", "4.6.1"),
            new PackageIdentity("Microsoft.Bcl.AsyncInterfaces", "9.0.0"),
            ]);
#endif

    internal static IEnumerable<MetadataReference> GetReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(JsonRpc).Assembly.Location);
#if POLYTYPE
        yield return MetadataReference.CreateFromFile(typeof(GenerateShapeAttribute).Assembly.Location);
#endif
        yield return MetadataReference.CreateFromFile(typeof(IDisposableObservable).Assembly.Location);
    }
}
