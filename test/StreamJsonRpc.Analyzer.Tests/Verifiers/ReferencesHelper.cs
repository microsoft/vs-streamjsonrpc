// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

internal static class ReferencesHelper
{
#if NET
    internal static ReferenceAssemblies References = ReferenceAssemblies.Net.Net80;
#else
    internal static ReferenceAssemblies References = ReferenceAssemblies.NetStandard.NetStandard20;
#endif

    internal static IEnumerable<MetadataReference> GetReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(JsonRpc).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(IDisposableObservable).Assembly.Location);
    }
}
