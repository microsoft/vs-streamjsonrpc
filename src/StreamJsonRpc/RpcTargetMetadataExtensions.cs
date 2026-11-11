#if NOTDOCFX // Workaround https://github.com/dotnet/docfx/issues/10808

using System.ComponentModel;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Extension methods for the <see cref="RpcTargetMetadata"/> class.
/// </summary>
public static class RpcTargetMetadataExtensions
{
    extension(RpcTargetMetadata)
    {
        /// <summary>
        /// Creates an <see cref="RpcTargetMetadata"/> instance from the specified shape.
        /// </summary>
        /// <typeparam name="T">The type for which a shape should be obtained and <see cref="RpcTargetMetadata"/> generated for.</typeparam>
        /// <returns>An <see cref="RpcTargetMetadata"/> instance initialized from the shape of the <typeparamref name="T"/>.</returns>
#if NET
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use the RpcTargetMetadata.FromShape<T>() method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
        public static RpcTargetMetadata FromShape<T>()
            => RpcTargetMetadata.FromShape(TypeShapeResolver.ResolveDynamicOrThrow<T>());

        /// <summary>
        /// Creates an <see cref="RpcTargetMetadata"/> instance from the specified shape.
        /// </summary>
        /// <typeparam name="T">The type for which a shape should be obtained and <see cref="RpcTargetMetadata"/> generated for.</typeparam>
        /// <typeparam name="TProvider">The provider of type shapes from which to obtain the shape.</typeparam>
        /// <returns>An <see cref="RpcTargetMetadata"/> instance initialized from the shape of the <typeparamref name="T"/>.</returns>
#if NET
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use the RpcTargetMetadata.FromShape<T, TProvider>() method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
        public static RpcTargetMetadata FromShape<T, TProvider>()
            => RpcTargetMetadata.FromShape(TypeShapeResolver.ResolveDynamicOrThrow<T, TProvider>());
    }
}

#endif
