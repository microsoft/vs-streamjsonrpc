// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace StreamJsonRpc.Tests;

internal static class UnreachableAssemblyTools
{
    /// <summary>
    /// Useful for tests to call before asserting conditions that depend on the UnreachableAssembly.dll
    /// actually being unreachable.
    /// </summary>
    internal static void VerifyUnreachableAssembly()
    {
        Assert.Throws<FileNotFoundException>(() => typeof(UnreachableAssembly.SomeUnreachableClass));
    }

    /// <summary>
    /// Initializes an <see cref="AssemblyLoadContext"/> with UnreachableAssembly.dll loaded into it.
    /// </summary>
    /// <param name="testName">The name to give the <see cref="AssemblyLoadContext"/>.</param>
    /// <returns>The new <see cref="AssemblyLoadContext"/>.</returns>
    internal static AssemblyLoadContext CreateContextForReachingTheUnreachable([CallerMemberName] string? testName = null)
    {
        AssemblyLoadContext alc = new(testName);
        alc.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "hidden", "UnreachableAssembly.dll"));
        return alc;
    }

    /// <summary>
    /// Translates a <see cref="MethodInfo"/> from one ALC into another ALC, so that it can be invoked
    /// within the context of the new ALC.
    /// </summary>
    /// <param name="alc">The <see cref="AssemblyLoadContext"/> to load the method into.</param>
    /// <param name="helperMethodInfo">The <see cref="MethodInfo"/> of the method in the caller's ALC to load into the given <paramref name="alc"/>.</param>
    /// <returns>The translated <see cref="MethodInfo"/>.</returns>
    internal static MethodInfo LoadHelperInAlc(AssemblyLoadContext alc, MethodInfo helperMethodInfo)
    {
        Assembly selfWithinAlc = alc.LoadFromAssemblyPath(helperMethodInfo.DeclaringType!.Assembly.Location);
        MethodInfo helperWithinAlc = (MethodInfo)selfWithinAlc.ManifestModule.ResolveMethod(helperMethodInfo.MetadataToken)!;
        return helperWithinAlc;
    }
}

#endif
