// Copyright (c) Microsoft Corporation. All rights reserved.

using System.Reflection;

namespace StreamJsonRpc;

internal class AssemblyNameEqualityComparer : IEqualityComparer<AssemblyName>
{
    internal static readonly IEqualityComparer<AssemblyName> Instance = new AssemblyNameEqualityComparer();

    private AssemblyNameEqualityComparer()
    {
    }

    public bool Equals(AssemblyName? x, AssemblyName? y)
    {
        if (x is null && y is null)
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.FullName, y.FullName, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(AssemblyName obj)
    {
        Requires.NotNull(obj, nameof(obj));

        return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FullName);
    }
}
