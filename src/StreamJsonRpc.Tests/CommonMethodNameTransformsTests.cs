// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using StreamJsonRpc;
using Xunit;

public class CommonMethodNameTransformsTests
{
    [Fact]
    public void CamelCase()
    {
        Assert.Equal("fireOne", CommonMethodNameTransforms.CamelCase("FireOne"));
        Assert.Equal("fireOne", CommonMethodNameTransforms.CamelCase("fireOne"));
        Assert.Throws<ArgumentNullException>(() => CommonMethodNameTransforms.CamelCase(null));
        Assert.Equal(string.Empty, CommonMethodNameTransforms.CamelCase(string.Empty));
    }

    [Fact]
    public void Prefix()
    {
        Assert.Equal("FooDo", CommonMethodNameTransforms.Prepend("Foo")("Do"));
        Assert.Equal("Foo.Do", CommonMethodNameTransforms.Prepend("Foo.")("Do"));
        Assert.Equal("Foo.Bar/Do", CommonMethodNameTransforms.Prepend("Foo.Bar/")("Do"));
        Assert.Equal("Foo.Bar.Do", CommonMethodNameTransforms.Prepend("Foo.Bar.")("Do"));
        Assert.Throws<ArgumentNullException>(() => CommonMethodNameTransforms.Prepend(null));
        Assert.Equal("Do", CommonMethodNameTransforms.Prepend(string.Empty)("Do"));
    }
}
