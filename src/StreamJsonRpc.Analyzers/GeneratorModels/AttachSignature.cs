namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal enum AttachSignature
{
    /// <summary>
    /// The <c>Attach&lt;T&gt;()</c> method.
    /// </summary>
    InstanceGeneric,

    /// <summary>
    /// The <c>Attach&lt;T&gt;(JsonRpcProxyOptions? options)</c> method.
    /// </summary>
    InstanceGenericOptions,
}
