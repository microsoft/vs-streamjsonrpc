// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using System.Runtime.CompilerServices;

public class ArchitectureTests
{
    /// <summary>
    /// Verifies that no interceptors are present in the StreamJsonRpc assembly.
    /// </summary>
    /// <remarks>
    /// Our own library intentionally has NativeAOT safe and unsafe APIs, and the unsafe ones should
    /// <em>not</em> be rewritten to call the safe APIs, since that can cause runtime exceptions when
    /// our callers are not prepared for NativeAOT safety's restrictions.
    /// </remarks>
    [Fact]
    public void NoInterceptors()
    {
        Assert.DoesNotContain(typeof(JsonRpc).Assembly.GetTypes(), t => t.Namespace == "StreamJsonRpc.Generated" && t.Name != "StreamJsonRpc_Reflection_ProxyBase_IObserverProxyGenerator_Proxy`1" && t.GetCustomAttribute<CompilerGeneratedAttribute>() is null);
    }
}
