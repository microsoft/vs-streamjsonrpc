namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal enum AttachSignature
{
    /// <summary>
    /// The <c>Attach(Type)</c> method.
    /// </summary>
    InstanceNonGeneric,

    /// <summary>
    /// The <c>Attach(Type, JsonRpcProxyOptions?)</c> method.
    /// </summary>
    InstanceNonGenericOptions,

    /// <summary>
    /// The <c>Attach&lt;T&gt;()</c> method.
    /// </summary>
    InstanceGeneric,

    /// <summary>
    /// The <c>Attach&lt;T&gt;(JsonRpcProxyOptions?)</c> method.
    /// </summary>
    InstanceGenericOptions,

    /// <summary>
    /// The <c>Attach(ReadOnlySpan&lt;Type&gt;, JsonRpcProxyOptions?)</c> method.
    /// </summary>
    InstanceNonGenericSpanOptions,
}
