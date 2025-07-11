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

    /// <summary>
    /// The <c>Attach&lt;T&gt;(Stream)</c> method.
    /// </summary>
    StaticGenericStream,

    /// <summary>
    /// The <c>Attach&lt;T&gt;(Stream, Stream)</c> method.
    /// </summary>
    StaticGenericStreamStream,

    /// <summary>
    /// The <c>Attach&lt;T&gt;(IJsonRpcMessageHandler)</c> method.
    /// </summary>
    StaticGenericHandler,

    /// <summary>
    /// The <c>Attach&lt;T&gt;(IJsonRpcMessageHandler, JsonRpcProxyOptions?)</c> method.
    /// </summary>
    StaticGenericHandlerOptions,
}
